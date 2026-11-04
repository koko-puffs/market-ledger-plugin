using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;
using MarketLedger.Core;
using MarketLedger.Session;

namespace MarketLedger.Game;

/// <summary>The character you're playing: who, where, the crafting and gathering jobs' levels, gil and room. Framework thread.</summary>
public static class PlayerReader
{
    private static List<(string Abbreviation, ClassJob Row)>? jobs;

    /// <summary>The ClassJob rows of the jobs the site plans with (CRP … CUL, MIN, BTN, FSH).</summary>
    private static List<(string, ClassJob)> JobRows()
    {
        if (jobs != null) return jobs;
        var list = new List<(string, ClassJob)>();
        foreach (var row in Services.Data.GetExcelSheet<ClassJob>())
        {
            var abbr = row.Abbreviation.ExtractText();
            if (MarketLedger.Core.Jobs.Wanted(abbr)) list.Add((abbr, row));
        }
        jobs = list;
        return list;
    }

    /// <summary>Each crafting and gathering job's level ("CRP" → 100); a job not unlocked is left out.</summary>
    public static Dictionary<string, int> JobLevels()
    {
        var levels = new Dictionary<string, int>();
        if (!Services.PlayerState.IsLoaded) return levels;
        foreach (var (abbr, row) in JobRows())
        {
            try
            {
                var level = Services.PlayerState.GetClassJobLevel(row);
                if (level > 0) levels[abbr] = level;
            }
            catch (Exception ex)
            {
                Services.Log.Debug(ex, $"Couldn't read the level of {abbr}");
            }
        }
        return levels;
    }

    /// <summary>The character now, or null when you aren't logged in.</summary>
    public static CharacterSnapshot? Read(string salt)
    {
        var ps = Services.PlayerState;
        if (!ps.IsLoaded || ps.ContentId == 0 || !Services.ClientState.IsLoggedIn) return null;
        return new CharacterSnapshot(ps.CharacterName, InGame.HomeWorld(), InGame.Gil(), InGame.FreeSlots())
        {
            Key = SiteLink.CharacterKey(ps.ContentId, salt),
            DataCentre = InGame.DataCentre(),
            Jobs = JobLevels(),
        };
    }
}
