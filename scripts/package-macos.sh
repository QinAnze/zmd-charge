#!/usr/bin/env bash
#
# EndfieldCharge —— macOS .app / .dmg 打包脚本
#
# 用法：
#   ./scripts/package-macos.sh                    # ad-hoc 签名，产出 .app + .dmg
#   VERSION=1.2.3 ./scripts/package-macos.sh      # 指定版本号
#   RID=osx-x64 ./scripts/package-macos.sh        # Intel Mac
#   SIGNING_IDENTITY="Developer ID Application: X (TEAMID)" \
#     NOTARY_PROFILE=AC_PASSWORD ./scripts/package-macos.sh   # 正式签名 + 公证
#   NO_DMG=1 ./scripts/package-macos.sh           # 跳过 dmg
#
# 步骤：publish → 组装 bundle → 写 Info.plist → 签名 → dmg → 公证 → **--selftest 冒烟**
# 最后一步是硬性门禁：IOKit / CoreFoundation / AppKit 互操作在干净环境不可用时，
# 打包阶段就会失败，而不是等用户下载后才发现。

set -euo pipefail

# macOS 自带 bash 3.2 按字节解析变量名：$VAR 紧跟中日韩字符时会吞掉该字符，
# 配合 set -u 报 "unbound variable"。固定 locale 并全程使用 ${VAR} 花括号形式。
export LC_ALL=C.UTF-8
export LANG=C.UTF-8

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
APP_NAME="EndfieldCharge"
BUNDLE_ID="com.lenkmat.endfieldcharge"

# ---------- 版本号：VERSION 环境变量 > git tag > CI run number > 兜底 ----------
if [[ -z "${VERSION:-}" ]]; then
  if [[ "${GITHUB_REF:-}" == refs/tags/v* ]]; then
    VERSION="${GITHUB_REF#refs/tags/v}"
  elif TAG="$(git -C "$REPO_ROOT" describe --tags --abbrev=0 2>/dev/null)"; then
    VERSION="${TAG#v}"
  elif [[ -n "${GITHUB_RUN_NUMBER:-}" ]]; then
    VERSION="0.0.${GITHUB_RUN_NUMBER}"
  else
    VERSION="0.1.0-dev"
  fi
fi

# ---------- 目标架构：与当前机器一致 ----------
if [[ -z "${RID:-}" ]]; then
  case "$(uname -m)" in
    arm64) RID="osx-arm64" ;;
    x86_64) RID="osx-x64" ;;
    *) echo "!! 不支持的架构：$(uname -m)" >&2; exit 1 ;;
  esac
fi

BUILD_DIR="$REPO_ROOT/build/macos"
PUBLISH_DIR="$BUILD_DIR/publish"
DIST_DIR="$BUILD_DIR/dist"
APP_DIR="$DIST_DIR/${APP_NAME}.app"
DMG_PATH="$DIST_DIR/${APP_NAME}-${VERSION}-${RID}.dmg"

echo "==> EndfieldCharge macOS 打包"
echo "    version : $VERSION"
echo "    rid     : $RID"
echo "    app     : $APP_DIR"

rm -rf "$PUBLISH_DIR" "$APP_DIR"
mkdir -p "$PUBLISH_DIR" "$DIST_DIR"

# ---------- 1) publish ----------
echo "==> [1/7] dotnet publish"
dotnet publish "$REPO_ROOT/EndfieldCharge.csproj" \
  -c Release \
  -f net10.0 \
  -r "$RID" \
  -o "$PUBLISH_DIR" \
  -p:Version="$VERSION" \
  -p:SelfContained=true \
  -p:UseAppHost=true \
  -p:PublishSingleFile=false \
  --nologo

# ---------- 2) 组装 bundle ----------
echo "==> [2/7] 组装 .app bundle"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"

cp -R "$PUBLISH_DIR"/. "$APP_DIR/Contents/MacOS/"
rm -f "$APP_DIR/Contents/MacOS"/*.pdb || true

# 菜单栏图标（TrayIcon 从 avares 读取，这里额外放一份供 Finder/文档使用）
if [[ -f "$REPO_ROOT/Assets/tray_bolt.png" ]]; then
  cp "$REPO_ROOT/Assets/tray_bolt.png" "$APP_DIR/Contents/Resources/tray_bolt.png"
fi

# ---------- 3) Info.plist ----------
echo "==> [3/7] 写入 Info.plist（版本 ${VERSION}）"
sed "s/@@VERSION@@/$VERSION/g" "$BUILD_DIR/Info.plist" > "$APP_DIR/Contents/Info.plist"

chmod +x "$APP_DIR/Contents/MacOS/${APP_NAME}"

# ---------- 4) 签名 ----------
echo "==> [4/7] 代码签名"
SIGN_TARGET="${SIGNING_IDENTITY:--}"
if [[ "$SIGN_TARGET" == "-" ]]; then
  echo "    (未提供 SIGNING_IDENTITY，使用 ad-hoc 签名)"
fi

codesign --force --deep --options runtime \
  --entitlements "$BUILD_DIR/EndfieldCharge.entitlements" \
  --sign "$SIGN_TARGET" \
  --timestamp=none \
  "$APP_DIR"

codesign --verify --verbose=2 "$APP_DIR"
plutil -lint "$APP_DIR/Contents/Info.plist"

# ---------- 5) dmg ----------
if [[ -z "${NO_DMG:-}" ]]; then
  echo "==> [5/7] 生成 dmg"
  rm -f "$DMG_PATH"
  ln -sfn /Applications "$DIST_DIR/Applications"
  hdiutil create \
    -volname "$APP_NAME $VERSION" \
    -srcfolder "$DIST_DIR" \
    -ov -format UDZO \
    "$DMG_PATH" >/dev/null
  rm -f "$DIST_DIR/Applications"
else
  echo "==> [5/7] 跳过 dmg（NO_DMG 已设置）"
fi

# ---------- 6) 公证（可选） ----------
if [[ -n "${NOTARY_PROFILE:-}" && "$SIGN_TARGET" != "-" ]]; then
  echo "==> [6/7] 公证（notarytool profile=${NOTARY_PROFILE}）"
  ZIP_PATH="$DIST_DIR/${APP_NAME}-${VERSION}-${RID}.zip"
  rm -f "$ZIP_PATH"
  ditto -c -k --keepParent "$APP_DIR" "$ZIP_PATH"
  xcrun notarytool submit "$ZIP_PATH" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP_DIR"
  rm -f "$ZIP_PATH"
else
  echo "==> [6/7] 跳过公证（需 SIGNING_IDENTITY + NOTARY_PROFILE）"
fi

# ---------- 7) 冒烟：打包即可运行 ----------
echo "==> [7/7] 冒烟测试：--selftest"
SELFTEST_OUT="$(mktemp)"
trap 'rm -f "$SELFTEST_OUT"' EXIT

if ! "$APP_DIR/Contents/MacOS/${APP_NAME}" --selftest > "$SELFTEST_OUT" 2>&1; then
  echo "!! selftest 退出码非 0，输出如下：" >&2
  cat "$SELFTEST_OUT" >&2
  exit 1
fi

cat "$SELFTEST_OUT"

if grep -q '"fatalError"' "$SELFTEST_OUT"; then
  echo "!! selftest 报告 fatalError（见上方输出）" >&2
  exit 1
fi

if ! grep -q '"platform": "macOS"' "$SELFTEST_OUT"; then
  echo "!! selftest 未报告 macOS 平台，打包产物可能不对" >&2
  exit 1
fi

echo
echo "✅ 打包完成"
echo "    app: $APP_DIR"
[[ -f "$DMG_PATH" ]] && echo "    dmg: $DMG_PATH"
echo
echo "    安装：把 EndfieldCharge.app 拖入「应用程序」"
echo "    首次打开若被 Gatekeeper 拦截："
echo "      xattr -dr com.apple.quarantine /Applications/EndfieldCharge.app"
