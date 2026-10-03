using System.Text;

using TriasDev.Tabular.Csv;
using TriasDev.Tabular.Tests.Fixtures;

using Xunit;

namespace TriasDev.Tabular.Tests.Writing;

/// <summary>
/// What the writer promises regardless of format: the order of calls, the stream it is given, and
/// that it touches that stream only asynchronously.
/// </summary>
public sealed class TabularWriterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Csv(WriteTarget target) => Encoding.UTF8.GetString(target.ToArray());

    private static TabularWriter Writer(WriteTarget target, CsvWriterOptions? csv = null, bool leaveOpen = false) =>
        TabularWriter.Create(target, TabularFormat.Csv, new TabularWriterOptions { Csv = csv ?? CsvWriterOptions.Default, LeaveOpen = leaveOpen });

    [Fact]
    public async Task WritesAHeaderAndRowsAsUtf8WithAByteOrderMark()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target))
        {
            writer.BeginSheet("data", [new("Id"), new("Name"), new("Active")]);
            writer.BeginRow();
            writer.Write("1");
            writer.Write("Grüße");
            writer.Write(true);
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        Assert.Equal("﻿Id,Name,Active\r\n1,Grüße,true\r\n", Csv(target));
        Assert.True(target.IsDisposed);
        Assert.False(target.DisposedSynchronously);
    }

    [Fact]
    public async Task LeavesTheByteOrderMarkOutWhenAsked()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target, new CsvWriterOptions { ByteOrderMark = false }))
        {
            writer.BeginSheet("data", [new("a")]);
            await writer.CompleteAsync(Token);
        }

        Assert.Equal("a\r\n", Csv(target));
    }

    [Fact]
    public async Task QuotesWhatWouldOtherwiseBreakTheRecord()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target, new CsvWriterOptions { ByteOrderMark = false }))
        {
            writer.BeginSheet("data", [new("a"), new("b"), new("c"), new("d")]);
            writer.BeginRow();
            writer.Write("x,y");
            writer.Write("say \"hi\"");
            writer.Write("two\nlines");
            writer.Write("plain");
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        Assert.Equal("a,b,c,d\r\n\"x,y\",\"say \"\"hi\"\"\",\"two\nlines\",plain\r\n", Csv(target));
    }

    [Fact]
    public async Task PadsAShortRowAndWritesNothingForEmptyCells()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target, new CsvWriterOptions { ByteOrderMark = false }))
        {
            writer.BeginSheet("data", [new("a"), new("b"), new("c")]);
            writer.BeginRow();
            writer.Write((string?)null);
            writer.WriteEmpty();
            writer.EndRow();
            writer.BeginRow();
            writer.Write("x");
            writer.EndRow();
            await writer.CompleteAsync(Token);
        }

        Assert.Equal("a,b,c\r\n,,\r\nx,,\r\n", Csv(target));
    }

    [Fact]
    public async Task TouchesTheTargetOnlyAsynchronously()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target))
        {
            writer.BeginSheet("data", [new("a")]);

            for (int i = 0; i < 50_000; i++)
            {
                writer.BeginRow();
                writer.Write("a value long enough to pass the flush threshold well before the end");
                writer.EndRow();

                if (writer.FlushRecommended)
                {
                    await writer.FlushAsync(Token);
                }
            }

            await writer.CompleteAsync(Token);
        }

        Assert.True(target.AsyncFlushes >= 1);
        Assert.False(target.DisposedSynchronously);
    }

    [Fact]
    public async Task RecommendsAFlushPastAMegabyteAndNotAfterIt()
    {
        WriteTarget target = new();
        await using TabularWriter writer = Writer(target);
        writer.BeginSheet("data", [new("a")]);
        string value = new('x', 1000);

        while (!writer.FlushRecommended)
        {
            writer.BeginRow();
            writer.Write(value);
            writer.EndRow();
        }

        Assert.Equal(0, target.Length);

        await writer.FlushAsync(Token);

        Assert.False(writer.FlushRecommended);
        Assert.True(target.Length >= 1024 * 1024);
    }

    [Fact]
    public async Task DisposingWithoutCompletingWritesNothingMore()
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target))
        {
            writer.BeginSheet("data", [new("a")]);
            writer.BeginRow();
            writer.Write("pending");
            writer.EndRow();
        }

        Assert.Empty(target.ToArray());
        Assert.True(target.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosesTheTargetUnlessToldToLeaveItOpen(bool leaveOpen)
    {
        WriteTarget target = new();

        await using (TabularWriter writer = Writer(target, leaveOpen: leaveOpen))
        {
            writer.BeginSheet("data", [new("a")]);
            await writer.CompleteAsync(Token);
        }

        Assert.Equal(!leaveOpen, target.IsDisposed);
    }

    [Fact]
    public async Task DisposingTwiceIsHarmless()
    {
        WriteTarget target = new();
        TabularWriter writer = Writer(target, leaveOpen: true);
        writer.BeginSheet("data", [new("a")]);
        await writer.CompleteAsync(Token);

        await writer.DisposeAsync();
        await writer.DisposeAsync();

        Assert.False(target.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => writer.BeginRow());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACreateThatFailsFollowsLeaveOpen(bool leaveOpen)
    {
        MemoryStream target = new();

        Assert.ThrowsAny<ArgumentException>(() => TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Delimiter = ':' }, LeaveOpen = leaveOpen }));

        Assert.Equal(leaveOpen, target.CanWrite);
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    [InlineData(TabularFormat.Zip)]
    public void RefusesAFormatItCannotWriteYet(TabularFormat format)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TabularWriter.Create(new MemoryStream(), format));
    }

    [Fact]
    public void RefusesAStreamThatCannotBeWritten()
    {
        Assert.Throws<ArgumentException>(() => TabularWriter.Create(new MemoryStream([], writable: false), TabularFormat.Csv));
    }

    [Fact]
    public async Task AFailingTargetFaultsTheWriterAndDisposalStillClosesIt()
    {
        WriteTarget target = new() { FailWith = new IOException("client went away") };
        TabularWriter writer = Writer(target);
        writer.BeginSheet("data", [new("a")]);

        await Assert.ThrowsAsync<IOException>(async () => await writer.FlushAsync(Token));
        Assert.Throws<InvalidOperationException>(() => writer.BeginRow());

        await writer.DisposeAsync();
        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task ACancelledFlushFaultsTheWriter()
    {
        WriteTarget target = new();
        TabularWriter writer = Writer(target);
        writer.BeginSheet("data", [new("a")]);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer.FlushAsync(cancelled.Token));
        Assert.Throws<InvalidOperationException>(() => writer.BeginRow());
        Assert.Empty(target.ToArray());

        await writer.DisposeAsync();
    }

    public static TheoryData<string, Action<TabularWriter>> Misuses => new()
    {
        { "a value outside a row", w => { w.BeginSheet("data", [new("a")]); w.Write("x"); } },
        { "a row before a sheet", w => w.BeginRow() },
        { "ending a row never begun", w => { w.BeginSheet("data", [new("a")]); w.EndRow(); } },
        { "more values than columns", w => { w.BeginSheet("data", [new("a")]); w.BeginRow(); w.Write("x"); w.Write("y"); } },
        { "a second csv sheet", w => { w.BeginSheet("data", [new("a")]); w.BeginSheet("more", [new("a")]); } },
        { "a sheet inside a row", w => { w.BeginSheet("data", [new("a")]); w.BeginRow(); w.BeginSheet("more", [new("a")]); } },
    };

    [Theory]
    [MemberData(nameof(Misuses))]
    public async Task RefusesCallsOutOfOrderAndStaysRefused(string misuse, Action<TabularWriter> calls)
    {
        Assert.NotEmpty(misuse);
        await using TabularWriter writer = Writer(new WriteTarget());

        Assert.Throws<InvalidOperationException>(() => calls(writer));
        Assert.Throws<InvalidOperationException>(() => writer.BeginSheet("again", [new("a")]));
    }

    public static TheoryData<string, WriteColumn[]> BadColumns => new()
    {
        { "no columns", [] },
        { "an empty header", [new("")] },
        { "a whitespace header", [new("  ")] },
        { "a missing header", [default] },
        { "a header repeated", [new("Name"), new(" name ")] },
        { "a zero width", [new("a", 0)] },
        { "a width past Excel's", [new("a", 256)] },
        { "too many columns", [.. Enumerable.Range(0, 16_385).Select(i => new WriteColumn($"c{i}"))] },
    };

    [Theory]
    [MemberData(nameof(BadColumns))]
    public async Task RefusesColumnsThatCouldNotBeReadBack(string problem, WriteColumn[] columns)
    {
        Assert.NotEmpty(problem);
        await using TabularWriter writer = Writer(new WriteTarget());

        Assert.ThrowsAny<ArgumentException>(() => writer.BeginSheet("data", columns));
    }

    [Fact]
    public async Task CompletingInsideARowOrWithoutASheetIsRefused()
    {
        await using TabularWriter empty = Writer(new WriteTarget());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await empty.CompleteAsync(Token));

        await using TabularWriter midRow = Writer(new WriteTarget());
        midRow.BeginSheet("data", [new("a")]);
        midRow.BeginRow();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await midRow.CompleteAsync(Token));
    }

    [Fact]
    public async Task NothingCanBeWrittenAfterCompleting()
    {
        await using TabularWriter writer = Writer(new WriteTarget());
        writer.BeginSheet("data", [new("a")]);
        await writer.CompleteAsync(Token);

        Assert.Throws<InvalidOperationException>(() => writer.BeginRow());
    }
}
