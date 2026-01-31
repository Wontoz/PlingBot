namespace PlingBot.Utils;
using System;

public class Logger
{
    public void Log(string message, ConsoleColor color = ConsoleColor.Gray)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        Console.ForegroundColor = prev;
    }

    public void Error(string message) => Log($"ERROR: {message}", ConsoleColor.Red);
    public void Info(string message) => Log(message, ConsoleColor.White);
}