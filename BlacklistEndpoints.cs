using System.Globalization;

namespace EmqxMonitor;

/// <summary>黑名单 / 身份控制开关 / 包头审计事件端点</summary>
public static class BlacklistEndpoints
{
    public static void MapBlacklistEndpoints(this WebApplication app, EmqxClient emqx, Database db,
        CollectorService collector, TopicIngestService topicIngest, AppSettings settings, WhitelistStore whitelist)
    {
        // ---- 黑名单 API（拉黑/解封/当前生效/操作历史）----
        // 权威执行在 EMQX（banned API），本地 blacklist_audit 留痕；EMQX 操作失败不写流水。

        // POST /api/blacklist/ban — 拉黑呼号（username 粒度）+ 立即踢下线 + 留痕
        app.MapPost("/api/blacklist/ban", async (BlacklistBanRequest req, HttpContext ctx) =>
        {
            var who = req.Who?.Trim() ?? "";
            if (string.IsNullOrEmpty(who))
                return Results.Json(new { ok = false, error = "呼号不能为空" });
            if (!emqx.IsConfigured)
                return Results.Json(new { ok = false, error = "未配置 EMQX 连接，无法执行拉黑" });

            // 到期时间：本地 yyyy-MM-ddTHH:mm → RFC3339（含 +08:00 偏移，EMQX 自动转 UTC 存储）
            string? untilRfc = null, untilLocal = null;
            if (!string.IsNullOrWhiteSpace(req.Until))
            {
                if (!DateTime.TryParseExact(req.Until, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var until))
                    return Results.Json(new { ok = false, error = "到期时间格式应为 yyyy-MM-ddTHH:mm" });
                if (until <= DateTime.Now)
                    return Results.Json(new { ok = false, error = "到期时间必须晚于当前时间" });
                var offset = TimeZoneInfo.Local.GetUtcOffset(until);
                untilRfc = until.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ssK").Replace("+00:00", $"{(offset >= TimeSpan.Zero ? "+" : "-")}{offset:hh\\:mm}");
                untilLocal = until.ToString("yyyy-MM-dd HH:mm:ss");
            }

            var (err, kicked) = await emqx.BanAsync(who, req.Reason?.Trim() is { Length: > 0 } r ? r : null, untilRfc);
            if (err != null)
                return Results.Json(new { ok = false, error = $"拉黑失败: {err}" });

            db.AddBlacklistEvent("ban", "username", who, req.Reason?.Trim(), untilLocal,
                ctx.User.Identity?.Name ?? "?", DateTime.Now);
            // 拉黑后即时刷新在线列表缓存（被踢的客户端立即从"在线"消失）
            _ = collector.CollectNowAsync();
            return Results.Json(new { ok = true, who, kicked, until = untilLocal });
        });

        // POST /api/blacklist/unban — 解封呼号 + 留痕
        app.MapPost("/api/blacklist/unban", async (BlacklistUnbanRequest req, HttpContext ctx) =>
        {
            var who = req.Who?.Trim() ?? "";
            if (string.IsNullOrEmpty(who))
                return Results.Json(new { ok = false, error = "呼号不能为空" });
            if (!emqx.IsConfigured)
                return Results.Json(new { ok = false, error = "未配置 EMQX 连接，无法执行解封" });

            var err = await emqx.UnbanAsync(who);
            if (err != null)
                return Results.Json(new { ok = false, error = $"解封失败: {err}" });

            db.AddBlacklistEvent("unban", "username", who, null, null,
                ctx.User.Identity?.Name ?? "?", DateTime.Now);
            _ = collector.CollectNowAsync();   // 即时刷新在线列表缓存
            return Results.Json(new { ok = true, who });
        });

        // GET /api/blacklist/active — 当前生效黑名单（本地推导）+ EMQX 侧对照（用于发现 EMQX 上手动拉的黑名单）
        app.MapGet("/api/blacklist/active", async () =>
        {
            var local = db.QueryActiveBlacklist(DateTime.Now);
            var emqxBanned = emqx.IsConfigured ? await emqx.GetBannedAsync() : [];
            return Results.Json(new
            {
                ok = true,
                // 本地生效：带全字段（操作人/时间/到期）
                local,
                // EMQX 侧存在但本地无记录（可能是 EMQX 上手动拉黑）→ 前端提示"来源: EMQX"
                emqx_only = emqxBanned.Where(b => !local.Any(l => string.Equals(l.Who, b.Who, StringComparison.OrdinalIgnoreCase)))
                                      .Select(b => new { b.Who, b.Reason, b.Until, b.By }),
                emqx_reachable = emqx.IsConfigured,
            });
        });

        // GET /api/blacklist/history — 全部操作流水（倒序）
        app.MapGet("/api/blacklist/history", (int? limit, Database database) =>
            Results.Json(new { ok = true, rows = database.QueryBlacklistHistory(Math.Clamp(limit ?? 200, 1, 1000)) }));

        // GET /api/identity-control — 身份控制开关状态（默认启用）
        app.MapGet("/api/identity-control", () => Results.Json(new
        {
            ok = true,
            enabled = topicIngest.IdentityControlEnabled,
        }));

        // POST /api/identity-control — 设置身份控制开关（关闭 = 降级为仅标记提醒，不自动拉黑）
        app.MapPost("/api/identity-control", (IdentityControlRequest req) =>
        {
            topicIngest.IdentityControlEnabled = req.Enabled;
            settings.IdentityControlEnabled = req.Enabled;
            return Results.Json(new { ok = true, enabled = req.Enabled });
        });

        // GET /api/audit-packets — 包头审计事件（KICK/WARN/FAIL）
        app.MapGet("/api/audit-packets", (string from, string to, string? verdict, int? limit, Database database) =>
        {
            var (f, t, err) = WebHelpers.ParseRange(from, to);
            if (err != null) return Results.Json(new { ok = false, error = err });
            var rows = database.QueryAuditPackets(f, t, verdict, Math.Clamp(limit ?? 200, 1, 1000));
            var counts = database.CountAuditVerdicts(f, t);
            return Results.Json(new { ok = true, from = f, to = t, rows, counts });
        });

        // ---- 白名单 API（免于身份审计）—— 白名单功能 © BG2GZK ----

        // GET /api/whitelist — 白名单列表（按添加时间倒序）
        app.MapGet("/api/whitelist", (Database database) =>
            Results.Json(new { ok = true, rows = database.QueryWhitelist() }));

        // POST /api/whitelist — 添加白名单呼号（大写归一，重复添加幂等）
        app.MapPost("/api/whitelist", (WhitelistAddRequest req, HttpContext ctx, Database database) =>
        {
            var cs = req.Callsign?.Trim() ?? "";
            if (string.IsNullOrEmpty(cs))
                return Results.Json(new { ok = false, error = "呼号不能为空" });
            if (cs.Length > 12)
                return Results.Json(new { ok = false, error = "呼号过长（包头呼号字段上限 12 字符）" });

            var added = database.AddWhitelist(cs, req.Note?.Trim(), ctx.User.Identity?.Name ?? "?", DateTime.Now);
            whitelist.Invalidate();
            return Results.Json(new { ok = true, callsign = cs.ToUpperInvariant(), added });
        });

        // POST /api/whitelist/remove — 移除白名单呼号（移除后恢复身份审计）
        app.MapPost("/api/whitelist/remove", (WhitelistRemoveRequest req, Database database) =>
        {
            var cs = req.Callsign?.Trim() ?? "";
            if (string.IsNullOrEmpty(cs))
                return Results.Json(new { ok = false, error = "呼号不能为空" });

            var removed = database.RemoveWhitelist(cs);
            whitelist.Invalidate();
            return Results.Json(new { ok = true, removed });
        });
    }
}
