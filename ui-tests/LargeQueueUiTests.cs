using System.IO.Compression;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace diashow.UiTests;

public sealed class LargeQueueUiTests : UiTestBase
{
    private const int QueueSize = 100_000;

    public LargeQueueUiTests() : base(CreateLargeQueue)
    {
    }

    [Fact]  
    public void PreviewIsGeneratedAndSlideshowAdvancesWithLargeQueue()
    {
        WaitUntil(() => MainWindow.FindAllDescendants()
            .Any(element => GetName(element) == $"1 / {QueueSize}"), TimeSpan.FromMinutes(2));

        MainWindow.SetForeground();
        MainWindow.Focus();
        Keyboard.Press(VirtualKeyShort.TAB);

        WaitUntil(() => MainWindow.FindAllDescendants() 
            .Any(IsQueuePreviewItem),
            TimeSpan.FromSeconds(10));

        var previewItems = MainWindow.FindAllDescendants()
            .Count(IsQueuePreviewItem);
        Assert.InRange(previewItems, 3, 20);
        Assert.Contains(MainWindow.FindAllDescendants(),
            element => GetName(element)?.Contains("000001.png", StringComparison.Ordinal) == true);

        MainWindow.SetForeground();
        MainWindow.Focus();
        Keyboard.Press(VirtualKeyShort.RIGHT);

        WaitUntil(() => MainWindow.FindAllDescendants()
            .Any(element => GetName(element) == $"2 / {QueueSize}"), TimeSpan.FromSeconds(10));

        MainWindow.SetForeground();
        MainWindow.Focus();
        Keyboard.Press(VirtualKeyShort.RIGHT);

        WaitUntil(() => MainWindow.FindAllDescendants()
            .Any(element => GetName(element) == $"3 / {QueueSize}"), TimeSpan.FromSeconds(10));
    }

    private static (string Path, Action Cleanup) CreateLargeQueue()
    {
        var folder = Path.Combine(
            Path.GetTempPath(), $"diashow-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        for (var index = 1; index <= QueueSize; index++)
            WriteNumberPng(Path.Combine(folder, $"{index:D6}.png"), index);

        return (Path.Combine(folder, "000001.png"), () =>
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        });
    }

    private static void WriteNumberPng(string path, int number)
    {
        const int width = 160;
        const int height = 48;
        var stride = width * 3 + 1;
        var pixels = new byte[height * stride];
        for (var offset = 1; offset < pixels.Length; offset++)
            pixels[offset] = 255;

        var text = number.ToString();
        var xStart = (width - text.Length * 6) / 2;
        for (var index = 0; index < text.Length; index++)
        {
            var glyph = Digits[text[index] - '0'];
            for (var row = 0; row < glyph.Length; row++)
            for (var column = 0; column < glyph[row].Length; column++)
            {
                if (glyph[row][column] != '#')
                    continue;
                var offset = (20 + row) * stride + 1 + (xStart + index * 6 + column) * 3;
                pixels[offset] = 0;
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
            }
        }
        
        using var output = File.Create(path);
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WriteChunk(output, "IHDR", [.. BigEndian(width), .. BigEndian(height), 8, 2, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(pixels);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(BigEndian(data.Length));
        output.Write(typeBytes);
        output.Write(data);
        output.Write(BigEndian(Crc32(typeBytes, data)));
    }

    private static byte[] BigEndian(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] BigEndian(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xffffffffu;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;   
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static readonly string[][] Digits =
    [
        ["###", "#.#", "#.#", "#.#", "###"],
        ["..#", "..#", "..#", "..#", "..#"],
        ["###", "..#", "###", "#..", "###"],
        ["###", "..#", "###", "..#", "###"],
        ["#.#", "#.#", "###", "..#", "..#"],
        ["###", "#..", "###", "..#", "###"],
        ["###", "#..", "###", "#.#", "###"],
        ["###", "..#", "..#", "..#", "..#"],
        ["###", "#.#", "###", "#.#", "###"],
        ["###", "#.#", "###", "..#", "###"]
    ];

    private static string? GetName(AutomationElement element)
    {
        try { return element.Name; }
        catch (PropertyNotSupportedException) { return null; }
    }

    private static bool IsQueuePreviewItem(AutomationElement element) =>
        element.ControlType == ControlType.Button &&
        GetName(element)?.Contains(".png", StringComparison.OrdinalIgnoreCase) == true;

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            Thread.Sleep(100);
        }

        Assert.True(condition(), "The expected UI state was not reached before the timeout.");
    }
}
