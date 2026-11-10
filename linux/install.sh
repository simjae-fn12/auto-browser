#!/usr/bin/env bash
# Installs Oinzi for Linux from this checkout: dependencies, Chromium's sandbox helper, a launcher and a menu entry.
# Run from anywhere: ./linux/install.sh
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"

echo "의존성 설치 중..."
npm install --no-fund --no-audit

# Chromium's sandbox needs its setuid helper on distributions that restrict unprivileged user namespaces
# (Ubuntu 23.10+). Running without the sandbox (--no-sandbox) would be unsafe for a browser.
sandbox="$here/node_modules/electron/dist/chrome-sandbox"
if [ "$(stat -c '%U %a' "$sandbox")" != "root 4755" ]; then
  echo "Chromium 샌드박스 설정 (sudo 필요)..."
  sudo chown root:root "$sandbox"
  sudo chmod 4755 "$sandbox"
fi

bin="$HOME/.local/bin"
apps="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
mkdir -p "$bin" "$apps"
cat > "$bin/oinzi" <<EOF
#!/usr/bin/env bash
exec "$here/node_modules/electron/dist/electron" "$here" "\$@"
EOF
chmod +x "$bin/oinzi"

cat > "$apps/oinzi.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Oinzi
Comment=사람과 AI 에이전트가 함께 쓰는 브라우저
Exec=$bin/oinzi %U
Icon=$here/oinzi.png
Categories=Network;WebBrowser;
MimeType=text/html;x-scheme-handler/http;x-scheme-handler/https;
Terminal=false
EOF
update-desktop-database "$apps" 2>/dev/null || true

echo
echo "설치했습니다. 앱 메뉴의 Oinzi 또는 터미널에서 'oinzi'로 실행하세요."
case ":$PATH:" in *":$bin:"*) ;; *) echo "참고: $bin 이 PATH에 없습니다. 셸 설정에 추가하세요." ;; esac
