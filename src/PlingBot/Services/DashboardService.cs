namespace PlingBot.Services;

using Discord;
using Discord.WebSocket;
using PlingBot.Config;
using PlingBot.Utils;

public class DashboardService
{
    private readonly TipsConfig _tipsConfig;
    private readonly CouponEvaluator _evaluator;
    private readonly Logger _logger;

    private ulong? _channelId;
    private ulong? _messageId;
    private string? _lastContent;

    // Variabler som tillhör random meddelandet
    private string? _currentExtraMessage;
    private DateTime _lastExtraMessageChangedUtc = DateTime.MinValue;
    private static readonly TimeSpan ExtraMessageInterval = TimeSpan.FromMinutes(10);
    public DashboardService(TipsConfig tipsConfig, CouponEvaluator evaluator, Logger logger)
    {
        _tipsConfig = tipsConfig;
        _evaluator = evaluator;
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

            _logger.Log("Dashboard refreshed on startup", ConsoleColor.Cyan);
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

    public void AddEvent(string eventText)
    {
        _tipsConfig.Data.Events.Add(eventText);
        _tipsConfig.SaveToJson();
        _logger.Log($"Event added to list: {eventText}");
    }

    private string BuildContent(string? extraMessage = null)
    {
        if (extraMessage != null)
        {
            _currentExtraMessage = extraMessage;
            _lastExtraMessageChangedUtc = DateTime.UtcNow;
        }

        return _evaluator.BuildCouponStatusMessage(_tipsConfig, _currentExtraMessage, _tipsConfig.Data.Events);
    }

    public bool RefreshExtraMessageIfNeeded(StatusMessageService statusMessageService)
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