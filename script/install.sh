#!/usr/bin/env bash
# ============================================================
# FMO Audit Service（修改版，含白名单功能 © BG2GZK）一键安装脚本
# 用法: sudo bash install.sh <包路径或URL> [安装目录]
#   示例: sudo bash install.sh ./fmo-audit-service-linux-x64.tar.gz
#         sudo bash install.sh https://example.com/fas/fmo-audit-service-linux-x64.tar.gz
#   可用环境变量: FAS_PORT=9527  FAS_USER=fmo-audit
# 行为: 解压 → /opt/fmo-fas/ → 创建低权限用户 → 注册 systemd 服务 fmo-fas（开机自启+崩溃拉起）
# ============================================================
set -euo pipefail

PKG="${1:-}"
INSTALL_DIR="${2:-/opt/fmo-fas}"
SERVICE="fmo-fas"
USER_NAME="${FAS_USER:-fmo-audit}"
PORT="${FAS_PORT:-9527}"

info() { printf '\033[32m[+]\033[0m %s\n' "$*"; }
fail() { printf '\033[31m[!]\033[0m %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || fail "请用 root/sudo 运行（安装目录与 systemd 注册需要）"
[ -n "$PKG" ] || fail "用法: sudo bash install.sh <包路径或URL> [安装目录]"

# ---- 取包：URL 下载或本地复制 ----
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
case "$PKG" in
  http://*|https://*)
    info "下载 $PKG"
    curl -fsSL --retry 3 -o "$TMP/pkg.tar.gz" "$PKG" || fail "下载失败"
    ;;
  *)
    [ -f "$PKG" ] || fail "包不存在: $PKG"
    cp "$PKG" "$TMP/pkg.tar.gz"
    ;;
esac

mkdir -p "$TMP/x"
tar -xzf "$TMP/pkg.tar.gz" -C "$TMP/x"
BIN="$TMP/x/fmo-audit-service"
[ -f "$BIN" ] || fail "包内未找到 fmo-audit-service（请确认平台对应的包）"

# ---- 停旧服务（升级/重装场景）----
systemctl stop "$SERVICE" 2>/dev/null || true

# ---- 安装二进制 ----
mkdir -p "$INSTALL_DIR"
install -m 755 "$BIN" "$INSTALL_DIR/fmo-audit-service"
info "已安装到 $INSTALL_DIR/fmo-audit-service"

# ---- 专用低权限用户 ----
if ! id -u "$USER_NAME" &>/dev/null; then
  NOLOGIN="$(command -v nologin || echo /usr/sbin/nologin)"
  useradd -r -s "$NOLOGIN" "$USER_NAME" || fail "创建用户 $USER_NAME 失败"
fi
chown -R "$USER_NAME:$USER_NAME" "$INSTALL_DIR"

# ---- systemd unit（开机自启 + 崩溃自动拉起 + 沙箱加固）----
cat > "/etc/systemd/system/${SERVICE}.service" <<EOF
[Unit]
Description=FMO Audit Service (FAS)
After=network-online.target
Wants=network-online.target

[Service]
User=${USER_NAME}
WorkingDirectory=${INSTALL_DIR}
Environment=EMQX_MONITOR_DB=${INSTALL_DIR}/fmo-audit-service.db
Environment=EMQX_MONITOR_PORT=${PORT}
ExecStart=${INSTALL_DIR}/fmo-audit-service
Restart=on-failure
# 沙箱加固
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=${INSTALL_DIR}

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now "$SERVICE"
sleep 1
systemctl --no-pager -l status "$SERVICE" | head -n 5 || true

info "安装完成！访问 http://<本机IP>:${PORT}"
info "常用: systemctl status/restart ${SERVICE}；journalctl -u ${SERVICE} -f"
info "升级: sudo ${INSTALL_DIR}/fmo-audit-service --update && sudo systemctl restart ${SERVICE}"
