namespace Qadam1C;

internal static class CompletionRefreshPolicy
{
    internal static bool HideForKey(int key, bool control, bool alt) =>
        key is 9 or 13 or 27 or 33 or 34 or 35 or 36 or 37 or 39 || control || alt;

    // A visual may outlive its actionable snapshot during typing, but only for one failed poll.
    internal static bool KeepVisualOnMiss(IntPtr? visualOwner, IntPtr foreground, int misses) =>
        visualOwner is { } owner && owner != IntPtr.Zero && owner == foreground && misses < 2;
}
