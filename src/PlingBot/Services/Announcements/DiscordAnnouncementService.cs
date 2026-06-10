namespace PlingBot.Services;

using Discord;
using PlingBot.Models;
using PlingBot.Utils;

public class DiscordAnnouncementService
{
    private readonly DashboardService _dashboardService;
    private readonly Logger _logger;
    private readonly Dictionary<string, IUserMessage> _goalMessages = new();

    public DiscordAnnouncementService(DashboardService dashboardService, Logger logger)
    {
        _dashboardService = dashboardService;
        _logger = logger;
    }

    public async Task<IUserMessage> AnnounceAsync(
        IMessageChannel channel,
        string message,
        ConsoleColor logColor,
        string logPrefix,
        bool deleteAfterDelay = true,
        TimeSpan? deleteDelay = null,
        CouponEvent? couponEvent = null)
    {
        _dashboardService.AddEvent(couponEvent ?? new CouponEvent
        {
            Type = "Message",
            Text = message,
            CreatedUtc = DateTime.UtcNow
        });

        var sentMessage = await channel.SendMessageAsync(message);
        _logger.Log($"{logPrefix}: {message}", logColor);

        if (deleteAfterDelay)
            _ = DeleteMessageAsync(sentMessage, deleteDelay ?? TimeSpan.FromMinutes(1));

        return sentMessage;
    }

    public void TrackGoalMessage(string key, IUserMessage message)
    {
        _goalMessages[key] = message;
    }

    public async Task<bool> TryUpdateGoalMessageAsync(
        IMessageChannel channel,
        string key,
        string oldMessage,
        string newMessage,
        string? oldMessageFragment = null)
    {
        var sentMessage = await FindGoalMessageAsync(channel, key, oldMessage, oldMessageFragment);
        if (sentMessage == null)
            return false;

        if (sentMessage.Content == newMessage)
            return false;

        try
        {
            await sentMessage.ModifyAsync(m => m.Content = newMessage);
            _logger.Log($"Goal message updated: {newMessage}", ConsoleColor.Magenta);
            _ = DeleteMessageAsync(sentMessage, TimeSpan.FromMinutes(1));
            return true;
        }
        catch (Exception ex)
        {
            _goalMessages.Remove(key);
            _logger.Log($"Could not update goal message: {ex.Message}", ConsoleColor.DarkYellow);
            return false;
        }
    }

    private async Task<IUserMessage?> FindGoalMessageAsync(
        IMessageChannel channel,
        string key,
        string oldMessage,
        string? oldMessageFragment)
    {
        if (_goalMessages.TryGetValue(key, out var trackedMessage))
            return trackedMessage;

        try
        {
            var messages = await channel.GetMessagesAsync(50).FlattenAsync();
            var existingMessage = messages
                .OfType<IUserMessage>()
                .FirstOrDefault(message =>
                    message.Author.IsBot &&
                    (message.Content == oldMessage ||
                        (!string.IsNullOrWhiteSpace(oldMessageFragment) &&
                            message.Content.Contains(oldMessageFragment, StringComparison.Ordinal))));

            if (existingMessage != null)
                _goalMessages[key] = existingMessage;

            return existingMessage;
        }
        catch (Exception ex)
        {
            _logger.Log($"Could not find goal message to update: {ex.Message}", ConsoleColor.DarkYellow);
            return null;
        }
    }

    private async Task DeleteMessageAsync(IUserMessage message, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            await message.DeleteAsync();
            RemoveTrackedGoalMessage(message.Id);
        }
        catch (Exception ex)
        {
            RemoveTrackedGoalMessage(message.Id);
            _logger.Log($"Could not delete message: {ex.Message}", ConsoleColor.DarkYellow);
        }
    }

    private void RemoveTrackedGoalMessage(ulong messageId)
    {
        foreach (var key in _goalMessages
            .Where(pair => pair.Value.Id == messageId)
            .Select(pair => pair.Key)
            .ToList())
        {
            _goalMessages.Remove(key);
        }
    }
}
