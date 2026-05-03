#!/bin/sh
# Weave CLI installer for macOS and Linux
# Usage:  curl -fsSL https://raw.githubusercontent.com/KoalaFacts/Weave/main/scripts/install.sh | sh
# Pinned: WEAVE_VERSION=0.1.0 curl -fsSL ... | sh
set -e

REPO="KoalaFacts/Weave"
TOOL_NAME="weave"
INSTALL_DIR="${WEAVE_INSTALL_DIR:-$HOME/.weave/bin}"

status()  { printf '  \033[36m%s\033[0m\n' "$1"; }
success() { printf '  \033[32m%s\033[0m\n' "$1"; }
err()     { printf '  \033[31m%s\033[0m\n' "$1"; }

echo ""
printf '  \033[35mWeave CLI Installer\033[0m\n'
echo ""

# Detect OS
OS="$(uname -s)"
case "$OS" in
    Linux*)  OS_NAME="linux" ;;
    Darwin*) OS_NAME="osx" ;;
    *)
        err "Unsupported operating system: $OS"
        exit 1
        ;;
esac

# Detect architecture
ARCH="$(uname -m)"
case "$ARCH" in
    x86_64|amd64) ARCH_NAME="x64" ;;
    arm64|aarch64) ARCH_NAME="arm64" ;;
    *)
        err "Unsupported architecture: $ARCH"
        exit 1
        ;;
esac

RID="${OS_NAME}-${ARCH_NAME}"
status "Detected platform: $RID"

# Resolve version
if [ -n "${WEAVE_VERSION:-}" ]; then
    TAG="v${WEAVE_VERSION}"
    status "Using requested version: $TAG"
else
    status "Finding latest release..."
    if command -v curl > /dev/null 2>&1; then
        RELEASE_JSON=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest")
    elif command -v wget > /dev/null 2>&1; then
        RELEASE_JSON=$(wget -qO- "https://api.github.com/repos/$REPO/releases/latest")
    else
        err "Neither curl nor wget found. Please install one and try again."
        exit 1
    fi

    TAG=$(echo "$RELEASE_JSON" | grep '"tag_name"' | head -1 | sed 's/.*"tag_name": *"\([^"]*\)".*/\1/')
    if [ -z "$TAG" ]; then
        err "Could not determine latest release version."
        exit 1
    fi
    status "Latest version: $TAG"
fi

# Download
VERSION="${TAG#v}"
ASSET_NAME="weave-${VERSION}-${RID}.tar.gz"
DOWNLOAD_URL="https://github.com/$REPO/releases/download/$TAG/$ASSET_NAME"
TMP_FILE="$(mktemp)"

status "Downloading $ASSET_NAME..."
if command -v curl > /dev/null 2>&1; then
    curl -fsSL "$DOWNLOAD_URL" -o "$TMP_FILE"
else
    wget -qO "$TMP_FILE" "$DOWNLOAD_URL"
fi

# Install
status "Installing to $INSTALL_DIR..."
mkdir -p "$INSTALL_DIR"
tar -xzf "$TMP_FILE" -C "$INSTALL_DIR"
rm -f "$TMP_FILE"
chmod +x "$INSTALL_DIR/$TOOL_NAME"

if [ ! -f "$INSTALL_DIR/$TOOL_NAME" ]; then
    err "Installation failed — $INSTALL_DIR/$TOOL_NAME not found after extraction."
    exit 1
fi

# Add to PATH guidance
SHELL_NAME="$(basename "${SHELL:-/bin/sh}")"
case "$SHELL_NAME" in
    zsh)  PROFILE="$HOME/.zshrc" ;;
    bash) PROFILE="$HOME/.bashrc" ;;
    fish) PROFILE="$HOME/.config/fish/config.fish" ;;
    *)    PROFILE="$HOME/.profile" ;;
esac

case ":$PATH:" in
    *":$INSTALL_DIR:"*) ;;
    *)
        status "Adding $INSTALL_DIR to your PATH in $PROFILE..."
        if [ "$SHELL_NAME" = "fish" ]; then
            echo "fish_add_path $INSTALL_DIR" >> "$PROFILE"
        else
            echo "export PATH=\"$INSTALL_DIR:\$PATH\"" >> "$PROFILE"
        fi
        export PATH="$INSTALL_DIR:$PATH"
        ;;
esac

echo ""
success "Weave CLI $TAG installed successfully!"
echo ""
echo "  Get started:"
echo "    weave workspace new demo"
echo "    weave workspace up demo"
echo ""
echo "  You may need to restart your shell or run:"
echo "    source $PROFILE"
echo ""
