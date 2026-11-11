set shell := ["zsh", "-cu"]

default_port := "18765"

# 默认列出可用命令
default:
    @just --list

# 启动 Safe Code 并打开独立 GUI 窗口（自动停止旧实例）
run port=default_port:
    pkill -TERM -f "SafeCode.Web" 2>/dev/null || true
    sleep 0.5
    dotnet run --project src/SafeCode.Web/SafeCode.Web.csproj -- --port {{port}} --open-app

# 启动 Safe Code 并在默认浏览器标签页打开（自动停止旧实例）
web port=default_port:
    pkill -TERM -f "SafeCode.Web" 2>/dev/null || true
    sleep 0.5
    dotnet run --project src/SafeCode.Web/SafeCode.Web.csproj -- --port {{port}} --open-browser

# 仅启动后台 Web 服务（不自动弹窗）
serve port=default_port:
    pkill -TERM -f "SafeCode.Web" 2>/dev/null || true
    sleep 0.5
    dotnet run --project src/SafeCode.Web/SafeCode.Web.csproj -- --port {{port}}

# 编译项目
build:
    dotnet build src/SafeCode.Web/SafeCode.Web.csproj

# 运行本地单元测试
test:
    python3 tools/test_debian13_agent.py
    python3 tools/test_mount_targets.py
    python3 tools/test_ip_policy_transactions.py
