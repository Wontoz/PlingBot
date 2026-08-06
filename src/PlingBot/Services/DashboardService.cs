namespace PlingBot.Services;

using Discord;
using Discord.WebSocket;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class DashboardService
{
    private readonly TipsConfig tipsConfig;
    private readonly DashboardBuilder dashboardBuilder;
    private readonly VersusDashboardBuilder versusDashboardBuilder;
    private readonly VersusConfig versusConfig;
    private readonly BotOptions options;
    private readonly Logger _logger;

    private ulong? channelId;
    private ulong? messageId;
    private string? lastContent;

    // Variabler som tillhör random meddelandet
    private string? currentExtraMessage;
    private DateTime lastExtraMessageChangedUtc = DateTime.MinValue;
    private static readonly TimeSpan ExtraMessageInterval = TimeSpan.FromMinutes(10);
    public DashboardService(
        TipsConfig tipsConfig,
        DashboardBuilder dashboardBuilder,
        VersusDashboardBuilder versusDashboardBuilder,
        VersusConfig versusConfig,
        BotOptions options,
        Logger logger)
    {
        this.tipsConfig = tipsConfig;
        this.dashboardBuilder = dashboardBuilder;
        this.versusDashboardBuilder = versusDashboardBuilder;
        this.versusConfig = versusConfig;
        this.options = options;
        _logger = logger;
    }

    public async Task CreateOrUpdateAsync(IMessageChannel channel, string? extraMessage = null)
    {
        string content = BuildContent(extraMessage);

        if (content == lastContent)
            return;

        if (messageId.HasValue)
        {
            var existingMessage = await channel.GetMessageAsync(messageId.Value) as IUserMessage;

            if (existingMessage != null)
            {
                await existingMessage.ModifyAsync(m => m.Content = content);
                lastContent = content;
                return;
            }
        }

        var sentMessage = await channel.SendMessageAsync(content);

        channelId = channel.Id;
        messageId = sentMessage.Id;
        lastContent = content;

        _logger.Log("Dashboard message created", ConsoleColor.Cyan);
    }

    public async Task UpdateIfExistsAsync(DiscordSocketClient client)
    {
        if (!channelId.HasValue || !messageId.HasValue)
            return;

        var channel = client.GetChannel(channelId.Value) as IMessageChannel;
        if (channel == null)
            return;

        string content = BuildContent();

        if (content == lastContent)
            return;

        var existingMessage = await channel.GetMessageAsync(messageId.Value) as IUserMessage;
        if (existingMessage == null)
            return;

        await existingMessage.ModifyAsync(m => m.Content = content);

        lastContent = content;

        _logger.Log("Dashboard updated", ConsoleColor.Cyan);
    }

    public async Task DeletePreviousDashboardsAsync(IMessageChannel channel)
    {
        var messages = await channel.GetMessagesAsync(50).FlattenAsync();

        var dashboards = messages
            .Where(message =>
                message.Author.IsBot &&
                message.Content.StartsWith("```"))
            .Take(3);

        foreach (var dashboard in dashboards)
        {
            await dashboard.DeleteAsync();
            await Task.Delay(750);
        }

        messageId = null;
        channelId = null;
        lastContent = null;
    }

    public void AddEvent(CouponEvent couponEvent)
    {
        tipsConfig.Data.Events.Add(couponEvent);
        tipsConfig.SaveToJson();
        _logger.Log($"Event added to list: {couponEvent.Text}");
    }

    public CouponEvent? GetEventByKey(string key) =>
        tipsConfig.Data.Events.FirstOrDefault(e => e.Key == key);

    public bool UpdateEvent(string oldText, CouponEvent newEvent)
    {
        int index = tipsConfig.Data.Events.FindIndex(e => e.Text == oldText);
        if (index < 0)
            return false;

        return ReplaceEventIfChanged(index, newEvent);
    }

    public bool RemoveGoalEvent(int fixtureId, int? teamId, int elapsed)
    {
        int index = tipsConfig.Data.Events.FindIndex(e =>
            e.Type == "Goal" &&
            e.FixtureId == fixtureId &&
            e.TeamId == teamId &&
            Math.Abs(e.Elapsed - elapsed) <= 1);
        if (index < 0) return false;
        var removed = tipsConfig.Data.Events[index];
        tipsConfig.Data.Events.RemoveAt(index);
        tipsConfig.SaveToJson();
        _logger.Log($"Goal removed by VAR: {removed.Text}");
        return true;
    }

    public bool UpdateEventByKey(string key, CouponEvent newEvent)
    {
        int index = tipsConfig.Data.Events.FindIndex(e => e.Key == key);
        if (index < 0) return false;
        return ReplaceEventIfChanged(index, newEvent);
    }

    public bool UpdateEventContaining(string textFragment, CouponEvent newEvent)
    {
        int index = tipsConfig.Data.Events.FindIndex(e => e.Text.Contains(textFragment, StringComparison.Ordinal));
        if (index < 0)
            return false;

        return ReplaceEventIfChanged(index, newEvent);
    }

    private bool ReplaceEventIfChanged(int index, CouponEvent newEvent)
    {
        var existing = tipsConfig.Data.Events[index];
        bool textChanged = existing.Text != newEvent.Text;
        // Assist kommer senare än själva målet — uppdatera även om texten är oförändrad
        bool assistChanged = newEvent.Assist != null && existing.Assist != newEvent.Assist;
        if (!textChanged && !assistChanged)
            return false;

        newEvent.CreatedUtc = existing.CreatedUtc;
        tipsConfig.Data.Events[index] = newEvent;
        tipsConfig.SaveToJson();
        _logger.Log($"Event updated in list: {newEvent.Text}");
        return true;
    }

    private string BuildContent(string? extraMessage = null)
    {
        if (extraMessage != null)
        {
            currentExtraMessage = extraMessage;
            lastExtraMessageChangedUtc = DateTime.UtcNow;
        }

        if (options.IsVersusMode)
            return versusDashboardBuilder.Build(tipsConfig, versusConfig, tipsConfig.Data.Events);

        return dashboardBuilder.Build(tipsConfig, currentExtraMessage, tipsConfig.Data.Events);
    }

    public bool RefreshExtraMessageIfNeeded(PlayerMessageService statusMessageService)
    {
        if (DateTime.UtcNow - lastExtraMessageChangedUtc < ExtraMessageInterval)
            return false;

        string player = tipsConfig.Data.MetaData.Player;
        currentExtraMessage = statusMessageService.Generate(player);
        lastExtraMessageChangedUtc = DateTime.UtcNow;

        _logger.Log($"Dashboard message changed: {currentExtraMessage}", ConsoleColor.Cyan);
        return true;
    }
}
