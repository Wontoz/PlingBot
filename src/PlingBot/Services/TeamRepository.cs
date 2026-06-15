namespace PlingBot.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PlingBot.Models;
using PlingBot.Utils;

public class TeamRepository
{
    private readonly string _filePath;
    private readonly Logger _logger;
    private readonly Dictionary<string, TeamRecord> _byName;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public TeamRepository(Logger logger)
    {
        _logger = logger;
        _filePath = ResolveFilePath();
        _byName = Load();
        _logger.Log($"Team registry loaded: {_byName.Count} entries from {_filePath}", ConsoleColor.DarkCyan);
    }

    public TeamRecord? FindByName(string name)
        => _byName.TryGetValue(name, out var r) ? r : null;

    public void Upsert(string name, string apiName, int? id)
    {
        if (!_byName.TryGetValue(name, out var existing))
        {
            _byName[name] = new TeamRecord { Name = name, ApiName = apiName, Id = id };
            Save();
            _logger.Log($"Team registry: added '{name}' → '{apiName}' (id={id})", ConsoleColor.DarkGray);
            return;
        }

        bool changed = false;

        if (id.HasValue && existing.Id != id)
        {
            existing.Id = id;
            changed = true;
        }

        if (!string.Equals(existing.ApiName, apiName, StringComparison.OrdinalIgnoreCase))
        {
            existing.ApiName = apiName;
            changed = true;
        }

        if (changed)
        {
            Save();
            _logger.Log($"Team registry: updated '{name}' → '{apiName}' (id={id})", ConsoleColor.DarkGray);
        }
    }

    private Dictionary<string, TeamRecord> Load()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, TeamRecord>(StringComparer.OrdinalIgnoreCase);

        var json = File.ReadAllText(_filePath, Encoding.UTF8);
        var list = JsonSerializer.Deserialize<List<TeamRecord>>(json) ?? [];
        return list.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        var list = _byName.Values
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(list, WriteOptions), Encoding.UTF8);
    }

    private static string ResolveFilePath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            string projectDir = Path.Combine(current.FullName, "src", "PlingBot");
            if (Directory.Exists(projectDir))
                return Path.Combine(projectDir, "data", "teams.json");
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/PlingBot/data/teams.json.");
    }
}
