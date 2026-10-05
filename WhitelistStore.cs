namespace EmqxMonitor;

/// <summary>
/// 白名单内存缓存（免于身份审计）—— 白名单功能 © BG2GZK
/// 审计位于消息事件高频路径（每秒数百~数千条），逐包查库不可行；
/// 白名单变更极少，故懒加载全量集合 + 增删时失效重载，Contains 为纯内存查询。
/// </summary>
public class WhitelistStore
{
    private readonly Database _db;
    private readonly object _lock = new();
    private HashSet<string>? _cache;   // 大写归一呼号；null = 未加载

    public WhitelistStore(Database db) => _db = db;

    /// <summary>连接身份呼号是否在白名单中（匿名连接呼号为空恒为 false）</summary>
    public bool Contains(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return false;
        var cs = callsign.Trim().ToUpperInvariant();
        lock (_lock)
        {
            if (_cache == null)
            {
                _cache = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in _db.QueryWhitelist())
                    _cache.Add(row.Callsign);
            }
            return _cache.Contains(cs);
        }
    }

    /// <summary>增删/清空白名单后调用：下次查询重新从库加载</summary>
    public void Invalidate()
    {
        lock (_lock) _cache = null;
    }
}
