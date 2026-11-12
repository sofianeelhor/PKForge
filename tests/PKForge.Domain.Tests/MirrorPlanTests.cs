using Xunit;
using PKForge.Domain;

namespace PKForge.Domain.Tests;

public sealed class MirrorPlanTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    private static Dictionary<string, string> Map(params (string Path, string Hash)[] items) =>
        items.ToDictionary(i => i.Path, i => i.Hash, MirrorPlan.PathComparer);

    private static MirrorAction Single(Dictionary<string, string> local, Dictionary<string, string> remote, Dictionary<string, string>? synced) =>
        Assert.Single(MirrorPlan.Compute(local, remote, synced, Today));

    [Fact]
    public void Same_name_same_hash_is_a_no_op_with_or_without_history()
    {
        Assert.Empty(MirrorPlan.Compute(Map(("a.txt", "1")), Map(("a.txt", "1")), null, Today));
        Assert.Empty(MirrorPlan.Compute(Map(("a.txt", "1")), Map(("a.txt", "1")), Map(("a.txt", "0")), Today));
        Assert.Empty(MirrorPlan.Compute(Map(("a.txt", "AB")), Map(("A.TXT", "ab")), Map(), Today));
    }

    [Fact]
    public void Changed_here_only_is_pushed() =>
        Assert.Equal(new MirrorAction(MirrorActionKind.Push, "a"), Single(Map(("a", "2")), Map(("a", "1")), Map(("a", "1"))));

    [Fact]
    public void Changed_in_the_folder_only_is_pulled() =>
        Assert.Equal(new MirrorAction(MirrorActionKind.Pull, "a"), Single(Map(("a", "1")), Map(("a", "2")), Map(("a", "1"))));

    [Fact]
    public void New_here_is_pushed_and_new_there_is_pulled()
    {
        Assert.Equal(MirrorActionKind.Push, Single(Map(("a", "1")), Map(), Map()).Kind);
        Assert.Equal(MirrorActionKind.Pull, Single(Map(), Map(("a", "1")), Map()).Kind);
    }

    [Fact]
    public void Changed_on_both_sides_is_a_conflict_keeping_both()
    {
        var action = Single(Map(("Bank/01 - Box 1/025 - Pikachu.pk7", "2")), Map(("Bank/01 - Box 1/025 - Pikachu.pk7", "3")),
            Map(("Bank/01 - Box 1/025 - Pikachu.pk7", "1")));
        Assert.Equal(MirrorActionKind.Conflict, action.Kind);
        Assert.Equal("Bank/01 - Box 1/025 - Pikachu (conflict 2026-11-10).pk7", action.ConflictPath);
    }

    [Fact]
    public void Different_on_both_sides_with_no_history_is_a_conflict_never_an_overwrite()
    {
        Assert.Equal(MirrorActionKind.Conflict, Single(Map(("a.txt", "1")), Map(("a.txt", "2")), null).Kind);
        Assert.Equal(MirrorActionKind.Conflict, Single(Map(("a.txt", "1")), Map(("a.txt", "2")), Map()).Kind);
    }

    [Fact]
    public void Conflict_names_never_collide_with_an_existing_file()
    {
        var action = Single(Map(("a.txt", "1"), ("a (conflict 2026-11-10).txt", "9")), Map(("a.txt", "2"), ("a (conflict 2026-11-10).txt", "9")), null);
        Assert.Equal("a (conflict 2026-11-10 2).txt", action.ConflictPath);
        Assert.Equal("noext (conflict 2026-11-10)", MirrorPlan.ConflictName("noext", Today, new HashSet<string>()));
    }

    [Fact]
    public void Deleted_in_the_folder_and_unchanged_here_is_deleted_here() =>
        Assert.Equal(MirrorActionKind.DeleteLocal, Single(Map(("a", "1")), Map(), Map(("a", "1"))).Kind);

    [Fact]
    public void Deleted_here_and_unchanged_in_the_folder_is_deleted_there() =>
        Assert.Equal(MirrorActionKind.DeleteRemote, Single(Map(), Map(("a", "1")), Map(("a", "1"))).Kind);

    [Fact]
    public void A_deletion_never_wins_over_an_unsynced_change()
    {
        // Edited here, deleted in the folder: the edit goes back to the folder.
        Assert.Equal(MirrorActionKind.Push, Single(Map(("a", "2")), Map(), Map(("a", "1"))).Kind);
        // Edited in the folder, deleted here: the edit comes back here.
        Assert.Equal(MirrorActionKind.Pull, Single(Map(), Map(("a", "2")), Map(("a", "1"))).Kind);
    }

    [Fact]
    public void A_file_with_no_record_of_the_last_sync_is_new_never_a_deletion()
    {
        var synced = Map(("other", "1"));
        Assert.Equal(MirrorActionKind.Push, Single(Map(("a", "1")), Map(), synced).Kind);
        Assert.Equal(MirrorActionKind.Pull, Single(Map(), Map(("a", "1")), synced).Kind);
    }

    [Fact]
    public void First_sync_is_a_union_and_never_deletes()
    {
        var plan = MirrorPlan.Compute(
            Map(("only-here", "1"), ("both", "5"), ("differs", "1")),
            Map(("only-there", "2"), ("both", "5"), ("differs", "2")), null, Today);
        Assert.DoesNotContain(plan, a => a.Kind is MirrorActionKind.DeleteLocal or MirrorActionKind.DeleteRemote);
        Assert.Contains(new MirrorAction(MirrorActionKind.Push, "only-here"), plan);
        Assert.Contains(new MirrorAction(MirrorActionKind.Pull, "only-there"), plan);
        Assert.Equal(MirrorActionKind.Conflict, plan.Single(a => a.Path == "differs").Kind);
        Assert.Equal(3, plan.Count);
    }

    [Fact]
    public void Gone_from_both_sides_is_nothing() =>
        Assert.Empty(MirrorPlan.Compute(Map(), Map(), Map(("a", "1")), Today));

    [Fact]
    public void A_rename_in_the_folder_is_a_deletion_plus_an_addition()
    {
        var plan = MirrorPlan.Compute(Map(("old", "1")), Map(("new", "1")), Map(("old", "1")), Today);
        Assert.Equal([new MirrorAction(MirrorActionKind.Pull, "new"), new MirrorAction(MirrorActionKind.DeleteLocal, "old")],
            plan.OrderBy(a => a.Path, StringComparer.Ordinal));
    }

    [Fact]
    public void A_rename_here_is_a_deletion_plus_an_addition_there()
    {
        var plan = MirrorPlan.Compute(Map(("new", "1")), Map(("old", "1")), Map(("old", "1")), Today);
        Assert.Equal([new MirrorAction(MirrorActionKind.Push, "new"), new MirrorAction(MirrorActionKind.DeleteRemote, "old")],
            plan.OrderBy(a => a.Path, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_case_at_once_gives_one_action_per_path()
    {
        var plan = MirrorPlan.Compute(
            Map(("push", "2"), ("pull", "1"), ("conflict", "2"), ("dellocal", "1"), ("same", "1")),
            Map(("push", "1"), ("pull", "2"), ("conflict", "3"), ("delremote", "1"), ("same", "1")),
            Map(("push", "1"), ("pull", "1"), ("conflict", "1"), ("dellocal", "1"), ("delremote", "1"), ("same", "1")), Today);
        Assert.Equal(5, plan.Count);
        Assert.Equal(MirrorActionKind.Push, plan.Single(a => a.Path == "push").Kind);
        Assert.Equal(MirrorActionKind.Pull, plan.Single(a => a.Path == "pull").Kind);
        Assert.Equal(MirrorActionKind.Conflict, plan.Single(a => a.Path == "conflict").Kind);
        Assert.Equal(MirrorActionKind.DeleteLocal, plan.Single(a => a.Path == "dellocal").Kind);
        Assert.Equal(MirrorActionKind.DeleteRemote, plan.Single(a => a.Path == "delremote").Kind);
    }

    [Fact]
    public void Hash_is_stable_hex() =>
        Assert.Equal(MirrorPlan.Hash([1, 2, 3]), MirrorPlan.Hash([1, 2, 3]));
}
