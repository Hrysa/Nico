using System.Runtime.InteropServices;

namespace Engine.Graphics;

/// <summary>Reads and writes Win32 clipboard text through the Unicode clipboard format.</summary>
internal static class WindowsClipboard
{
    private const uint UnicodeTextFormat = 13;
    private const uint MoveableZeroInitializedMemory = 0x0042;

    /// <summary>Reads UTF-16 text from the Win32 clipboard.</summary>
    /// <param name="owner">Native window owning clipboard access.</param>
    /// <returns>Clipboard text, or null when Unicode text is unavailable.</returns>
    internal static string? GetText(IntPtr owner)
    {
        if (!IsClipboardFormatAvailable(UnicodeTextFormat) || !TryOpen(owner))
            return null;
        try
        {
            var handle = GetClipboardData(UnicodeTextFormat);
            if (handle == IntPtr.Zero)
                return null;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Writes UTF-16 text to the Win32 clipboard.</summary>
    /// <param name="owner">Native window owning clipboard access.</param>
    /// <param name="text">Text to store.</param>
    /// <returns>True when ownership of the clipboard allocation was transferred successfully.</returns>
    internal static bool SetText(IntPtr owner, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var byteCount = checked((nuint)((text.Length + 1) * sizeof(char)));
        var memory = GlobalAlloc(MoveableZeroInitializedMemory, byteCount);
        if (memory == IntPtr.Zero)
            return false;
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(memory);
            return false;
        }
        try
        {
            var characters = text.ToCharArray();
            Marshal.Copy(characters, 0, pointer, characters.Length);
            Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
        }
        finally
        {
            GlobalUnlock(memory);
        }
        if (!TryOpen(owner))
        {
            GlobalFree(memory);
            return false;
        }
        try
        {
            if (!EmptyClipboard())
                return false;
            if (SetClipboardData(UnicodeTextFormat, memory) == IntPtr.Zero)
                return false;
            memory = IntPtr.Zero;
            return true;
        }
        finally
        {
            if (memory != IntPtr.Zero)
                GlobalFree(memory);
            CloseClipboard();
        }
    }

    /// <summary>Opens the clipboard with brief nonblocking retries for transient ownership races.</summary>
    /// <param name="owner">Native window owning clipboard access.</param>
    /// <returns>True when the clipboard was opened.</returns>
    private static bool TryOpen(IntPtr owner)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (OpenClipboard(owner))
                return true;
            Thread.Yield();
        }
        return false;
    }

    /// <summary>Checks whether one clipboard format is currently available.</summary>
    /// <param name="format">Win32 clipboard format identifier.</param>
    /// <returns>True when the format is available.</returns>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    /// <summary>Opens the clipboard for one native owner.</summary>
    /// <param name="owner">Native owner window.</param>
    /// <returns>True on success.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    /// <summary>Closes the clipboard.</summary>
    /// <returns>True on success.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    /// <summary>Removes existing clipboard content.</summary>
    /// <returns>True on success.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    /// <summary>Gets a clipboard storage handle for one format.</summary>
    /// <param name="format">Win32 clipboard format identifier.</param>
    /// <returns>Global memory handle, or zero.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    /// <summary>Transfers a global memory handle to the clipboard.</summary>
    /// <param name="format">Win32 clipboard format identifier.</param>
    /// <param name="memory">Global memory handle.</param>
    /// <returns>Stored clipboard handle, or zero.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    /// <summary>Allocates movable global memory.</summary>
    /// <param name="flags">Allocation flags.</param>
    /// <param name="bytes">Allocation size in bytes.</param>
    /// <returns>Global memory handle, or zero.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    /// <summary>Locks a global memory handle.</summary>
    /// <param name="memory">Global memory handle.</param>
    /// <returns>Memory pointer, or zero.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    /// <summary>Unlocks a global memory handle.</summary>
    /// <param name="memory">Global memory handle.</param>
    /// <returns>True when the lock count reaches zero.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    /// <summary>Frees an untransferred global memory handle.</summary>
    /// <param name="memory">Global memory handle.</param>
    /// <returns>Zero on success.</returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
