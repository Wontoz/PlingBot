namespace PlingBot.Utils;

// Vilket Discord-kanal-ID boten ska prata i styrs av CHANNEL_MODE-miljövariabeln
// (t.ex. CHANNEL_MODE=LIVE läser DISCORD_CHANNEL_ID_LIVE). Delad mellan poll-loopen
// och kommandohanteraren, som båda behövde räkna ut samma sak var för sig.
public static class DiscordChannel
{
    public static readonly string EnvKey =
        $"DISCORD_CHANNEL_ID_{(Environment.GetEnvironmentVariable("CHANNEL_MODE") ?? "TEST").ToUpper()}";

    public static ulong? ResolveAllowedChannelId() =>
        ulong.TryParse(Environment.GetEnvironmentVariable(EnvKey), out var id) ? id : null;
}
