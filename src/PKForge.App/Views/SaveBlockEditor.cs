using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Gen 8/9 save-block browser: every SCBlock with its key, type and value. Only boolean and
/// single-number blocks are editable, in place and in their own type; objects and arrays are
/// read-only here (use Byte manipulation for raw bytes).
/// </summary>
public static class SaveBlockEditor
{
    private const string Title = "SAVE BLOCKS";

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!SaveBlockEditorService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, Title, "Block editing is for Sword/Shield, Legends: Arceus, Scarlet/Violet and Legends: Z-A.", "OK");
            return;
        }

        var slot = Math.Max(0, viewModel.SelectedSlot);
        int? current = null;
        while (true)
        {
            var blocks = SaveBlockEditorService.GetBlocks(session);
            var items = blocks.Select((block, index) => new PickItem(index, Label(block))).ToArray();
            var picked = await PickerMenu.ShowAsync(host, $"{Title} · {blocks.Count}", items, current);
            if (picked is null) return;
            current = picked.Id;
            var block = blocks[picked.Id];

            if (!block.Editable)
            {
                await EditorMenu.ShowAsync(host, $"BLOCK {block.Key:X8}",
                    $"{block.Name ?? "Unnamed block"}\n{block.Type} · {block.Size} bytes\n\nRead-only here: only bool and single-number blocks can be edited.", "Back");
                continue;
            }

            if (HardcoreMode.Blocks(SaveAction.WriteRawBytes, out var blocked))
            {
                await EditorMenu.ShowAsync(host, "HARDCORE MODE", blocked, "OK");
                continue;
            }

            Func<ISaveEngineSession, GenerationOutcome> write;
            if (block.Type == "Bool")
            {
                var next = block.Value != "true";
                var confirmed = await PadMenu.ConfirmAsync(host, $"BLOCK {block.Key:X8}",
                    $"{block.Name ?? "Unnamed block"}\nSet {block.Value} → {(next ? "true" : "false")}? A restore point is created first.",
                    next ? "Set true" : "Set false");
                if (!confirmed) continue;
                write = s => SaveBlockEditorService.SetBool(s, block.Key, next);
            }
            else
            {
                var text = await TextPopup.ShowLineAsync(host, $"BLOCK {block.Key:X8}",
                    $"{block.Type} value (e.g. 12 or 1.5)", block.Value ?? "");
                if (string.IsNullOrWhiteSpace(text) || text.Trim() == block.Value) continue;
                write = s => SaveBlockEditorService.SetNumber(s, block.Key, text);
            }

            await viewModel.RunMutationAsync(write, slot, refreshSlot: false, action: SaveAction.WriteRawBytes);
        }
    }

    private static string Label(SaveBlockEntry block)
    {
        var name = block.Name is null ? "" : $" {block.Name}";
        var value = block.Value is null ? $" · {block.Size} B" : $" = {block.Value}";
        return $"{block.Key:X8}{name} · {block.Type}{value}";
    }
}
