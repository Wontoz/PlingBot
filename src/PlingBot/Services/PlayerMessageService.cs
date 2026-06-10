namespace PlingBot.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using PlingBot.Config;

public class PlayerMessageService
{
    private readonly Queue<string> recentMessages = new();
    private const int RecentMessageLimit = 10;

    private bool isFirstMessage = true;

    private readonly Dictionary<string, string> firstStatusMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jonas"] = "NU KÖR VI JONAS, ÄNDA IN I GUCCNÄSET!",
        ["fredrik"] = "KOMIGEN NU WIBB DET SKA BARA SMÄLLA IDAG, ANNARS SMÄLLER DET PÅ BIG MOUNTAIN STREET",
        ["william"] = "KÖRVI WILLE DET SKA IN TILL VARJE PRIS, LIVET INKLUDERAT"
    };

    private readonly Dictionary<string, string[]> messageVaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jonas"] =
        [
            "Fredrik & William gör allt rätt medan du gör allt fel Jonas.",
            "EeeeeeeeEEEEEEEk!",
            "Stabilt ändå... för att vara Jonas.",
            "'Vad gör du Jocke?' - ett citat som fungerar både efter 15 sekunders jockeying och efter den här kupongen.",
            "Om du bara var lika duktig på tipset som AIK's backar är på att dra in den i eget mål.",
            "Dr.Mugg hade uppskattat denna kupongen. Den är verkligen SKIT",
            "Synd att du inte kan re-rolla dina rader...",
            "Vi är med på stryket, men vi har FAN INTE ROLIGT när du lägger"
        ],

        ["fredrik"] =
        [
            "PleeeeeEEEEASE Wibb!",
            "Man kan tro att Fredrik körde spegeln idag.",
            "Det här lutar mot en klassisk Fredrik-kupong.",
            "Vad ska man säga egentligen?........'FUCK!!' kanske.",
            "Jocke hade nog gjort det bättre med ögonbindel.",
            "Helt crap då.",
            "Hur många poddar lyssna du på för att komma fram till det här?",
            "WIB-BER-BOY LA LAAAA LA LA-LA"
        ],

        ["william"] =
        [
            "Suck...",
            "Kan ju knappast bli sämre än förra gången du körde William",
            "William försöker i alla fall.",
            "Det här är varför vi inte kan ha fina saker.... med din kupong har vi bevisligen fula saker iallafall",
            "Läste du ens kupongen?",
            "Bli inte orimligt arg över det här med, så som du blir på NHL CS LoL Valorant TF2 TFT Minecraft Hästar Casino med mera med meeeeeera!",
            "Du har ju mer problem med dina rader än vad jag har när ett mål blir bortdömt.",
            "Vad har stopptid och William gemensamt? Båda förstör alltid och hjälper aldrig."
        ],

        ["generic"] =
        [
            "Henrik Johansson hade gjort det bättre",
            "Stryktipset ringde och bad dig sluta.",
            "Det sjukaste är inte denna kupongen. Det är att du tittade på den och tänkte 'jo men den här känns fin'.",
            "Det här är inte en kupong. Det är ett hatbrott.",
            "Bud på repet när man ser det här...",
            "Du döms härmed till tolv timmars obligatorisk Hunt: Showdown.",
            "Inte ens AI kan rädda den här skiten."
        ]
    };

    public string Generate(string player)
    {
        if (isFirstMessage &&
            firstStatusMessages.TryGetValue(player, out var firstMessage))
        {
            isFirstMessage = false;

            RememberMessage(firstMessage);
            return firstMessage;
        }

        bool usePersonalMessage = Random.Shared.NextDouble() < 0.65;

        string primaryKey = usePersonalMessage ? player : "generic";
        string fallbackKey = usePersonalMessage ? "generic" : player;

        if (messageVaults.TryGetValue(primaryKey, out var messages))
            return PickRandomMessage(messages);

        if (messageVaults.TryGetValue(fallbackKey, out var fallbackMessages))
            return PickRandomMessage(fallbackMessages);

        return "";
    }

    private string PickRandomMessage(string[] messages)
    {
        var available = messages
            .Where(m => !recentMessages.Contains(m))
            .ToArray();

        if (available.Length == 0)
            available = messages;

        string selected = available[
            Random.Shared.Next(available.Length)
        ];

        RememberMessage(selected);

        return selected;
    }

    private void RememberMessage(string message)
    {
        recentMessages.Enqueue(message);

        while (recentMessages.Count > RecentMessageLimit)
            recentMessages.Dequeue();
    }
}