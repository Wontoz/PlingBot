namespace PlingBot.Services;

using Discord;
using Discord.WebSocket;
using PlingBot.Config;
using PlingBot.Models;
using PlingBot.Utils;

public class DashboardService
{
    private readonly TipsConfig _tipsConfig;
    private readonly DashboardBuilder _dashboardBuilder;
    private readonly VersusDashboardBuilder _versusDashboardBuilder;
    private readonly VersusConfig _versusConfig;
    private readonly BotOptions _options;
    private readonly Logger _logger;

    private ulong? _channelId;
    private ulong? _messageId;
    private string? _lastContent;

    // Variabler som tillhör random meddelandet
    private string? _currentExtraMessage;
    private DateTime _lastExtraMessageChangedUtc = DateTime.MinValue;
    private static readonly TimeSpan ExtraMessageInterval = TimeSpan.FromMinutes(10);
    public DashboardService(
        TipsConfig tipsConfig,
        DashboardBuilder dashboardBuilder,
        VersusDashboardBuilder versusDashboardBuilder,
        VersusConfig versusConfig,
        BotOptions options,
        Logger logger)
    {
        _tipsConfig = tipsConfig;
        _dashboardBuilder = dashboardBuilder;
        _versusDashboardBuilder = versusDashboardBuilder;
        _versusConfig = versusConfig;
        _options = options;
        _logger = logger;
    }

    public async Task CreateOrUpdateAsync(IMessageChannel channel, string? extraMessage = null)
    {
        string content = BuildContent(extraMessage);

        if (content == _lastContent)
            return;

        if (_messageId.HasValue)
        {
            var existingMessage = await channel.GetMessageAsync(_messageId.Value) as IUserMessage;

            if (existingMessage != null)
            {
                await existingMessage.ModifyAsync(m => m.Content = content);
                _lastContent = content;
                return;
            }
        }

        var sentMessage = await channel.SendMessageAsync(content);

        _channelId = channel.Id;
        _messageId = sentMessage.Id;
        _lastContent = content;

        _logger.Log("Dashboard message created", ConsoleColor.Cyan);
    }

    public async Task UpdateIfExistsAsync(DiscordSocketClient client)
    {
        if (!_channelId.HasValue || !_messageId.HasValue)
            return;

        var channel = client.GetChannel(_channelId.Value) as IMessageChannel;
        if (channel == null)
            return;

        string content = BuildContent();

        if (content == _lastContent)
            return;

        var existingMessage = await channel.GetMessageAsync(_messageId.Value) as IUserMessage;
        if (existingMessage == null)
            return;

        await existingMessage.ModifyAsync(m => m.Content = content);

        _lastContent = content;

        _logger.Log("Dashboard updated", ConsoleColor.Cyan);
    }

    public async Task RefreshOrCreateOnStartupAsync(IMessageChannel channel, string? extraMessage = null)
    {
        var messages = await channel.GetMessagesAsync(50).FlattenAsync();

        var dashboard = messages.FirstOrDefault(message =>
            message.Author.IsBot &&
            message.Content.TrimStart().StartsWith("```"));

        string content = BuildContent(extraMessage);

        if (dashboard is IUserMessage existingDashboard)
        {
            await existingDashboard.ModifyAsync(m => m.Content = content);

            _channelId = channel.Id;
            _messageId = existingDashboard.Id;
            _lastContent = content;

            return;
        }

        var sentMessage = await channel.SendMessageAsync(content);

        _channelId = channel.Id;
        _messageId = sentMessage.Id;
        _lastContent = content;

        _logger.Log("Dashboard created on startup", ConsoleColor.Cyan);
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

        _messageId = null;
        _channelId = null;
        _lastContent = null;
    }

    public void AddEvent(CouponEvent couponEvent)
    {
        _tipsConfig.Data.Events.Add(couponEvent);
        _tipsConfig.SaveToJson();
        _logger.Log($"Event added to list: {couponEvent.Text}");
    }

    public CouponEvent? GetEventByKey(string key) =>
        _tipsConfig.Data.Events.FirstOrDefault(e => e.Key == key);

    public bool UpdateEvent(string oldText, CouponEvent newEvent)
    {
        int index = _tipsConfig.Data.Events.FindIndex(e => e.Text == oldText);
        if (index < 0)
            return false;

        return ReplaceEventIfChanged(index, newEvent);
    }

    public bool RemoveGoalEvent(int fixtureId, int? teamId, int elapsed)
    {
        int index = _tipsConfig.Data.Events.FindIndex(e =>
            e.Type == "Goal" &&
            e.FixtureId == fixtureId &&
            e.TeamId == teamId &&
            Math.Abs(e.Elapsed - elapsed) <= 1);
        if (index < 0) return false;
        var removed = _tipsConfig.Data.Events[index];
        _tipsConfig.Data.Events.RemoveAt(index);
        _tipsConfig.SaveToJson();
        _logger.Log($"Goal removed by VAR: {removed.Text}");
        return true;
    }

    public bool UpdateEventByKey(string key, CouponEvent newEvent)
    {
        int index = _tipsConfig.Data.Events.FindIndex(e => e.Key == key);
        if (index < 0) return false;
        return ReplaceEventIfChanged(index, newEvent);
    }

    public bool UpdateEventContaining(string textFragment, CouponEvent newEvent)
    {
        int index = _tipsConfig.Data.Events.FindIndex(e => e.Text.Contains(textFragment, StringComparison.Ordinal));
        if (index < 0)
            return false;

        return ReplaceEventIfChanged(index, newEvent);
    }

    private bool ReplaceEventIfChanged(int index, CouponEvent newEvent)
    {
        var existing = _tipsConfig.Data.Events[index];
        bool textChanged = existing.Text != newEvent.Text;
        // Assist arrives later than the goal itself — update even if text is unchanged
        bool assistChanged = newEvent.Assist != null && existing.Assist != newEvent.Assist;
        if (!textChanged && !assistChanged)
            return false;

        newEvent.CreatedUtc = existing.CreatedUtc;
        _tipsConfig.Data.Events[index] = newEvent;
        _tipsConfig.SaveToJson();
        _logger.Log($"Event updated in list: {newEvent.Text}");
        return true;
    }

    private string BuildContent(string? extraMessage = null)
    {
        if (extraMessage != null)
        {
            _currentExtraMessage = extraMessage;
            _lastExtraMessageChangedUtc = DateTime.UtcNow;
        }

        if (_options.IsVersusMode)
            return _versusDashboardBuilder.Build(_tipsConfig, _versusConfig, _tipsConfig.Data.Events);

        return _dashboardBuilder.Build(_tipsConfig, _currentExtraMessage, _tipsConfig.Data.Events);
    }

    public bool RefreshExtraMessageIfNeeded(PlayerMessageService statusMessageService)
    {
        if (DateTime.UtcNow - _lastExtraMessageChangedUtc < ExtraMessageInterval)
            return false;

        string player = _tipsConfig.Data.MetaData.Player;
        _currentExtraMessage = statusMessageService.Generate(player);
        _lastExtraMessageChangedUtc = DateTime.UtcNow;

        _logger.Log($"Dashboard message changed: {_currentExtraMessage}", ConsoleColor.Cyan);
        return true;
    }
}
