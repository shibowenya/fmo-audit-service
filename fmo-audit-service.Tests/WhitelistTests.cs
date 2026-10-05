using Xunit;

namespace EmqxMonitor.Tests;

/// <summary>白名单（免于身份审计）持久层与缓存测试 —— 白名单功能 © BG2GZK</summary>
public class WhitelistTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"wl-test-{Guid.NewGuid():N}.db");
    private readonly Database _db;

    public WhitelistTests() => _db = new Database(_dbPath);

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void Add_大写归一_重复添加返回false()
    {
        Assert.True(_db.AddWhitelist("bg2gzk", "中继台", "admin", DateTime.Now));
        Assert.False(_db.AddWhitelist("BG2GZK", null, "admin", DateTime.Now));   // 大小写不同视为同一呼号

        var rows = _db.QueryWhitelist();
        var row = Assert.Single(rows);
        Assert.Equal("BG2GZK", row.Callsign);
        Assert.Equal("中继台", row.Note);
        Assert.Equal("admin", row.Operator);
    }

    [Fact]
    public void Remove_存在返回true_不存在返回false()
    {
        _db.AddWhitelist("BG2GZK", null, "admin", DateTime.Now);
        Assert.True(_db.RemoveWhitelist(" bg2gzk "));   // 带空白 + 小写也能删
        Assert.False(_db.RemoveWhitelist("BG2GZK"));
        Assert.Empty(_db.QueryWhitelist());
    }

    [Fact]
    public void Store_Contains_大小写不敏感_空呼号恒false()
    {
        var store = new WhitelistStore(_db);
        _db.AddWhitelist("BG2GZK", null, "admin", DateTime.Now);

        Assert.True(store.Contains("bg2gzk"));
        Assert.True(store.Contains(" BG2GZK "));
        Assert.False(store.Contains("BG5ESN"));
        Assert.False(store.Contains(null));
        Assert.False(store.Contains(""));
    }

    [Fact]
    public void Store_Invalidate_后重载新数据()
    {
        var store = new WhitelistStore(_db);
        _db.AddWhitelist("BG2GZK", null, "admin", DateTime.Now);
        Assert.True(store.Contains("BG2GZK"));

        _db.RemoveWhitelist("BG2GZK");
        Assert.True(store.Contains("BG2GZK"));   // 未失效 → 仍用旧缓存
        store.Invalidate();
        Assert.False(store.Contains("BG2GZK"));  // 失效后重载
    }

    [Fact]
    public void ClearAll_清空白名单()
    {
        _db.AddWhitelist("BG2GZK", null, "admin", DateTime.Now);
        _db.ClearAll();
        Assert.Empty(_db.QueryWhitelist());
    }
}
