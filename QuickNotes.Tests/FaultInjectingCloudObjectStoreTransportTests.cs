using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickNotes.App.Models.Sync;
using Xunit;

namespace QuickNotes.Tests;

public sealed class FaultInjectingCloudObjectStoreTransportTests
{
    [Fact]
    public async Task ListObjectsAsync_SortsKeysOrdinal_BeforeFilterAndTake()
    {
        var transport = new FaultInjectingCloudObjectStoreTransport();
        Put(transport, "z/keep");
        Put(transport, "a/keep");
        Put(transport, "m/skip-prefix");
        Put(transport, "a/zz");
        Put(transport, "a/aa");

        var page = await transport.ListObjectsAsync("a/", maxKeys: 2);

        Assert.Equal(new[] { "a/aa", "a/keep" }, page.Select(o => o.Key).ToArray());
    }

    [Fact]
    public async Task ListObjectsV2Async_NumericLookingKeys_UseOpaqueKeyTokens_NotIntegerIndexes()
    {
        var transport = new FaultInjectingCloudObjectStoreTransport { ForceMaxKeys = 1 };
        Put(transport, "10");
        Put(transport, "2");
        Put(transport, "a");

        var first = await transport.ListObjectsV2Async(new StorageListRequest { MaxKeys = 1 });
        Assert.True(first.IsTruncated);
        Assert.Equal(new[] { "10" }, first.Objects.Select(o => o.Key).ToArray());
        Assert.Equal("2", first.NextContinuationToken);
        Assert.True(int.TryParse(first.NextContinuationToken, out int parsed) && parsed == 2,
            "resume token is a numeric-looking key; it must not be treated as an index");

        var second = await transport.ListObjectsV2Async(new StorageListRequest
        {
            MaxKeys = 1,
            ContinuationToken = first.NextContinuationToken
        });
        Assert.Equal(new[] { "2" }, second.Objects.Select(o => o.Key).ToArray());
        Assert.Equal("a", second.NextContinuationToken);

        var third = await transport.ListObjectsV2Async(new StorageListRequest
        {
            MaxKeys = 1,
            ContinuationToken = second.NextContinuationToken
        });
        Assert.Equal(new[] { "a" }, third.Objects.Select(o => o.Key).ToArray());
        Assert.False(third.IsTruncated);
        Assert.Null(third.NextContinuationToken);
    }

    [Fact]
    public async Task ListObjectsV2Async_MutationBetweenPages_NoDuplicatesOrSkipsForStableRemainingKeys()
    {
        var transport = new FaultInjectingCloudObjectStoreTransport { ForceMaxKeys = 2 };
        Put(transport, "a");
        Put(transport, "b");
        Put(transport, "c");
        Put(transport, "d");
        Put(transport, "e");

        var first = await transport.ListObjectsV2Async(new StorageListRequest { MaxKeys = 2 });
        Assert.Equal(new[] { "a", "b" }, first.Objects.Select(o => o.Key).ToArray());
        string token = first.NextContinuationToken!;
        Assert.Equal("c", token);

        transport.Store.TryRemove("a", out _);
        transport.Store.TryRemove("d", out _);
        Put(transport, "0-before-cursor");
        Put(transport, "c1-inserted");

        var seen = new HashSet<string>(first.Objects.Select(o => o.Key), StringComparer.Ordinal);
        string? continuation = token;
        var remainingPages = new List<string>();
        while (!string.IsNullOrEmpty(continuation))
        {
            var page = await transport.ListObjectsV2Async(new StorageListRequest
            {
                MaxKeys = 2,
                ContinuationToken = continuation
            });
            foreach (var key in page.Objects.Select(o => o.Key))
            {
                Assert.True(seen.Add(key), "duplicate key across pages: " + key);
                remainingPages.Add(key);
            }

            continuation = page.IsTruncated ? page.NextContinuationToken : null;
        }

        Assert.Equal(new[] { "c", "c1-inserted", "e" }, remainingPages.ToArray());
        Assert.DoesNotContain("0-before-cursor", remainingPages);
        Assert.DoesNotContain("d", remainingPages);
        Assert.DoesNotContain("b", remainingPages);
        Assert.DoesNotContain("0-before-cursor", seen);
    }

    private static void Put(FaultInjectingCloudObjectStoreTransport transport, string key)
    {
        transport.Store[key] = new FaultInjectingCloudObjectStoreTransport.StoredObject
        {
            Content = Array.Empty<byte>(),
            ETag = key,
            LastModified = DateTimeOffset.UnixEpoch,
            ContentType = "application/json"
        };
    }
}
