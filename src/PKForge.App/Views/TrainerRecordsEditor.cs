using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Read-only dashboard for save-native trainer records. Record meanings vary by game,
/// so this deliberately exposes values without offering unsafe generic writes.</summary>
public static class TrainerRecordsEditor
{
    private const int PageSize = 24;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session)
    {
        var info = session.GetTrainerRecords();
        if (!info.Supported)
        {
            await EditorMenu.ShowAsync(host, "TRAINER RECORDS", "This game does not expose a supported trainer-record table.", "OK");
            return;
        }

        var page = 0;
        var pageCount = (info.Records.Count + PageSize - 1) / PageSize;
        while (true)
        {
            var entries = info.Records.Skip(page * PageSize).Take(PageSize);
            var options = entries.Select(Format).Select(text => new PadOption(text)).ToList();
            if (page > 0) options.Add(new PadOption("Previous page"));
            if (page < pageCount - 1) options.Add(new PadOption("Next page"));
            var choice = await EditorMenu.ShowAsync(host, $"TRAINER RECORDS · {page + 1}/{pageCount}",
                "Read-only: record meanings and safe limits differ by game.", options.ToArray());
            if (choice is null) return;
            if (choice == "Previous page") { page--; continue; }
            if (choice == "Next page") { page++; continue; }
        }
    }

    private static string Format(TrainerRecordEntry record) => record.Maximum == int.MaxValue
        ? $"Record {record.Index + 1} · {record.Value:N0}"
        : $"Record {record.Index + 1} · {record.Value:N0}/{record.Maximum:N0}";
}
