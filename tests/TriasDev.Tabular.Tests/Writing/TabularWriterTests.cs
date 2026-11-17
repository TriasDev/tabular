using System.Text;

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
    [InlineData(TabularFormat.Gzip)]
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
    public async Task AFailedCompleteFaultsTheWriter()
    {
        WriteTarget target = new();
        TabularWriter writer = Writer(target);
        writer.BeginSheet("data", [new("a")]);
        target.FailWith = new IOException("client went away");

        await Assert.ThrowsAsync<IOException>(async () => await writer.CompleteAsync(Token));

        InvalidOperationException again = await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CompleteAsync(Token));
        Assert.Contains("failed earlier", again.Message, StringComparison.Ordinal);
        Assert.Contains("failed earlier", Assert.Throws<InvalidOperationException>(() => writer.BeginRow()).Message, StringComparison.Ordinal);

        await writer.DisposeAsync();
    }

    [Fact]
    public async Task ABatchTheSheetCannotTakeIsRefusedAndFaultsTheWriter()
    {
        ColumnBatch wide = new();
        wide.Reset(1);
        wide.Add(new long[] { 1 });
        wide.Add(new long[] { 2 });
        wide.Add(new long[] { 3 });

        await using TabularWriter early = Writer(new WriteTarget());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await early.WriteBatchAsync(wide, Token));

        await using TabularWriter writer = Writer(new WriteTarget());
        writer.BeginSheet("data", [new("a"), new("b")]);

        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await writer.WriteBatchAsync(wide, Token));
        Assert.Contains("failed earlier", Assert.Throws<InvalidOperationException>(() => writer.BeginRow()).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TabularFormat.Xlsx)]
    [InlineData(TabularFormat.Ods)]
    [InlineData(TabularFormat.Zip)]
    public async Task ASheetCannotBeginInsideARowOfAWorkbook(TabularFormat format)
    {
        await using TabularWriter writer = TabularWriter.Create(new WriteTarget(), format);
        writer.BeginSheet("data", [new("a")]);
        writer.BeginRow();

        Assert.Throws<InvalidOperationException>(() => writer.BeginSheet("more", [new("a")]));
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
        { "a header repeated", [new("Name"), new("name")] },
        { "a header with leading whitespace", [new(" Name")] },
        { "a header with trailing whitespace", [new("Name ")] },
        { "a header ending in a tab", [new("Name\t")] },
        { "a header starting with a line break", [new("\nName")] },
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

        // Refused, and refused again as complete — not as a writer that failed.
        InvalidOperationException first = Assert.Throws<InvalidOperationException>(() => writer.BeginRow());
        InvalidOperationException second = Assert.Throws<InvalidOperationException>(() => writer.BeginRow());

        Assert.Contains("complete", first.Message, StringComparison.Ordinal);
        Assert.Equal(first.Message, second.Message);
        Assert.DoesNotContain("failed earlier", second.Message, StringComparison.Ordinal);
    }

    public static TheoryData<TabularFormat, string, TabularWriterOptions> NullFormatOptions => new()
    {
        { TabularFormat.Csv, nameof(TabularWriterOptions.Csv), new TabularWriterOptions { Csv = null! } },
        { TabularFormat.Xlsx, nameof(TabularWriterOptions.Xlsx), new TabularWriterOptions { Xlsx = null! } },
        { TabularFormat.Ods, nameof(TabularWriterOptions.Ods), new TabularWriterOptions { Ods = null! } },
        { TabularFormat.Zip, nameof(TabularWriterOptions.Zip), new TabularWriterOptions { Zip = null! } },
    };

    [Theory]
    [MemberData(nameof(NullFormatOptions))]
    public void ANullFormatOptionIsRefusedByName(TabularFormat format, string option, TabularWriterOptions options)
    {
        MemoryStream target = new();

        ArgumentNullException refused = Assert.Throws<ArgumentNullException>(() => TabularWriter.Create(target, format, options));

        Assert.Equal(option, refused.ParamName);
        Assert.False(target.CanWrite);
    }

    /// <summary>Every public call of a writer, each given arguments it would otherwise accept.</summary>
    public static TheoryData<string, Func<TabularWriter, Task>> EveryCall => new()
    {
        { "BeginSheet", w => Sync(() => w.BeginSheet("more", [new("a")])) },
        { "BeginSheet with options", w => Sync(() => w.BeginSheet("more", [new("a")], SheetOptions.Default)) },
        { "BeginRow", w => Sync(w.BeginRow) },
        { "RegisterStyle", w => Sync(() => w.RegisterStyle(new CellStyle { Wrap = true })) },
        { "Write(string)", w => Sync(() => w.Write("x")) },
        { "Write(string, style)", w => Sync(() => w.Write("x", default)) },
        { "Write(long)", w => Sync(() => w.Write(1L)) },
        { "Write(long, style)", w => Sync(() => w.Write(1L, default)) },
        { "Write(decimal)", w => Sync(() => w.Write(1m)) },
        { "Write(decimal, style)", w => Sync(() => w.Write(1m, default)) },
        { "Write(double)", w => Sync(() => w.Write(1.5)) },
        { "Write(double, style)", w => Sync(() => w.Write(1.5, default)) },
        { "Write(DateTime)", w => Sync(() => w.Write(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified))) },
        { "Write(DateTime, style)", w => Sync(() => w.Write(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified), default)) },
        { "Write(DateOnly)", w => Sync(() => w.Write(new DateOnly(2026, 1, 2))) },
        { "Write(DateOnly, style)", w => Sync(() => w.Write(new DateOnly(2026, 1, 2), default)) },
        { "Write(bool)", w => Sync(() => w.Write(true)) },
        { "Write(bool, style)", w => Sync(() => w.Write(true, default)) },
        { "WriteEmpty", w => Sync(w.WriteEmpty) },
        { "WriteEmpty(style)", w => Sync(() => w.WriteEmpty(default)) },
        { "Merge", w => Sync(() => w.Merge(1, 2)) },
        { "EndRow", w => Sync(w.EndRow) },
        { "WriteBatch", w => Sync(() => w.WriteBatch(OneCell())) },
        { "WriteBatchAsync", w => w.WriteBatchAsync(OneCell(), Token).AsTask() },
        { "FlushAsync", w => w.FlushAsync(Token).AsTask() },
        { "CompleteAsync", w => w.CompleteAsync(Token).AsTask() },
    };

    private static Task Sync(Action call)
    {
        call();
        return Task.CompletedTask;
    }

    private static ColumnBatch OneCell()
    {
        ColumnBatch batch = new();
        batch.Reset(1);
        batch.Add(new[] { 1L });
        return batch;
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task EveryCallAfterCompletingIsRefusedAsComplete(string call, Func<TabularWriter, Task> invoke)
    {
        await using TabularWriter writer = Writer(new WriteTarget());
        writer.BeginSheet("data", [new("a")]);
        await writer.CompleteAsync(Token);

        // Refused as complete, by the same message every time — not by the call's own order check,
        // which would fault the writer and say it "failed earlier" from then on.
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => invoke(writer));
        InvalidOperationException again = await Assert.ThrowsAsync<InvalidOperationException>(() => invoke(writer));

        Assert.True(refused.Message.Contains("is complete", StringComparison.Ordinal), $"{call}: {refused.Message}");
        Assert.Equal(refused.Message, again.Message);
    }

    [Theory]
    [MemberData(nameof(EveryCall))]
    public async Task EveryCallAfterDisposingIsRefusedAsDisposed(string call, Func<TabularWriter, Task> invoke)
    {
        TabularWriter writer = Writer(new WriteTarget());
        writer.BeginSheet("data", [new("a")]);
        writer.BeginRow();
        await writer.DisposeAsync();

        ObjectDisposedException refused = await Assert.ThrowsAsync<ObjectDisposedException>(() => invoke(writer));

        Assert.True(refused.ObjectName == typeof(TabularWriter).FullName, $"{call}: {refused.ObjectName}");
    }

    [Fact]
    public async Task ACancelledFlushWithNothingPendingStillFaultsTheWriter()
    {
        WriteTarget target = new();
        TabularWriter writer = Writer(target);
        writer.BeginSheet("data", [new("a")]);
        await writer.FlushAsync(Token);
        int written = target.AsyncWrites;
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer.FlushAsync(cancelled.Token));

        Assert.Equal(written, target.AsyncWrites);
        Assert.Contains("failed earlier", Assert.Throws<InvalidOperationException>(() => writer.BeginRow()).Message, StringComparison.Ordinal);
        await writer.DisposeAsync();
    }

    [Fact]
    public async Task ACancelledCompleteFaultsTheWriterThoughTheTargetIgnoresTheToken()
    {
        // Csv has nothing left to write once flushed, and this target's FlushAsync never looks at
        // the token: only the writer's own check can see the cancellation.
        WriteTarget target = new();
        TabularWriter writer = Writer(target);
        writer.BeginSheet("data", [new("a")]);
        await writer.FlushAsync(Token);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer.CompleteAsync(cancelled.Token));

        Assert.Equal(0, target.AsyncFlushes);
        InvalidOperationException again = await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CompleteAsync(Token));
        Assert.Contains("failed earlier", again.Message, StringComparison.Ordinal);
        await writer.DisposeAsync();
    }

    [Fact]
    public void AStreamThatFailsToCloseDoesNotHideWhyCreateFailed()
    {
        ThrowsOnDispose target = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => TabularWriter.Create(
            target,
            TabularFormat.Csv,
            new TabularWriterOptions { Csv = new CsvWriterOptions { Delimiter = ':' } }));

        Assert.True(target.DisposeAttempted);
    }

    /// <summary>A stream whose closing fails.</summary>
    private sealed class ThrowsOnDispose : MemoryStream
    {
        public bool DisposeAttempted { get; private set; }

        protected override void Dispose(bool disposing)
        {
            DisposeAttempted = true;
            base.Dispose(disposing);
            throw new IOException("closing failed");
        }
    }

    [Fact]
    public async Task AFailedWriteKeepsItsExceptionWhenTheStreamAlsoFailsToClose()
    {
        WriteTarget target = new() { FailWith = new IOException("client went away"), FailOnDispose = new IOException("close failed") };

        IOException surfaced = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv);
            writer.BeginSheet("data", [new("a")]);
            writer.BeginRow();
            writer.Write("x");
            writer.EndRow();
            await writer.FlushAsync(Token);
        });

        Assert.Equal("client went away", surfaced.Message);
    }

    [Fact]
    public async Task AnAbandonedFileDropsTheCloseFailure()
    {
        WriteTarget target = new() { FailOnDispose = new IOException("close failed") };
        TabularWriter writer = TabularWriter.Create(target, TabularFormat.Xlsx);
        writer.BeginSheet("data", [new("a")]);

        await writer.DisposeAsync();

        Assert.True(target.IsDisposed);
    }

    [Fact]
    public async Task ACompletedFileStillReportsAFailedClose()
    {
        WriteTarget target = new() { FailOnDispose = new IOException("close failed") };
        TabularWriter writer = TabularWriter.Create(target, TabularFormat.Csv);
        writer.BeginSheet("data", [new("a")]);
        await writer.CompleteAsync(Token);

        IOException surfaced = await Assert.ThrowsAsync<IOException>(async () => await writer.DisposeAsync());
        Assert.Equal("close failed", surfaced.Message);
    }
}
