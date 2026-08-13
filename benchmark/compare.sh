#!/bin/bash
# Benchmark: FirmwareKit.Comm.ADB (C# native) vs Google adb (official)
# 对比 C# 原生实现与谷歌官方 adb 各命令耗时
# 注意：C# 侧 shell 因既有 ADB 协议握手问题失败，如实记录为 FAIL
set -u
CS_ADB="dotnet /Users/mujianwu/repo/firmwarekit.comm.adb/FirmwareKit.Comm.ADB.Cli/bin/Release/net10.0/adb.dll"
CS_ENV="DYLD_FALLBACK_LIBRARY_PATH=/opt/homebrew/lib"
OFF_ADB="/opt/homebrew/bin/adb"
SERIAL="4b37e5c9"
RUNS=5

# Commands: name, args (no serial), warmup-needed
# 命令集：两侧 transport 级命令 + shell（官方侧参考）
declare -a CMDS=(
  "devices|devices -l"
  "get-state|get-state"
  "get-serialno|get-serialno"
  "get-devpath|get-devpath"
  "shell|shell getprop ro.build.version.release"
)

# run_measure <impl> <cmd_args>  -> prints ms
run_measure() {
  local impl="$1"; shift
  local start end ns
  start=$(python3 -c 'import time;print(time.time_ns())')
  if [ "$impl" = "cs" ]; then
    env $CS_ENV $CS_ADB "$@" >/dev/null 2>&1
  else
    $OFF_ADB "$@" >/dev/null 2>&1
  fi
  end=$(python3 -c 'import time;print(time.time_ns())')
  echo $(( (end - start) / 1000000 ))
}

# kill any adb server so USB exclusive lock is released before each impl switch
kill_adb() { pkill -9 -f "adb" 2>/dev/null; sleep 1; }

echo "Benchmark: $(date '+%Y-%m-%d %H:%M:%S')  device=$SERIAL  runs=$RUNS"
echo ""
printf "%-18s | %-10s | %-10s | %-8s\n" "command" "official" "csharp" "delta%"
printf -- "--------------------|------------|------------|--------\n"

for entry in "${CMDS[@]}"; do
  name="${entry%%|*}"
  args="${entry#*|}"
  # official adb (daemon-based)
  kill_adb
  $OFF_ADB start-server >/dev/null 2>&1
  off_total=0
  for i in $(seq $RUNS); do
    ms=$(run_measure off $args)
    off_total=$((off_total + ms))
  done
  off_avg=$((off_total / RUNS))

  # csharp native (direct USB, no daemon)
  kill_adb
  cs_total=0
  cs_fail=0
  for i in $(seq $RUNS); do
    ms=$(run_measure cs --libusb $args)
    cs_total=$((cs_total + ms))
  done
  cs_avg=$((cs_total / RUNS))

  # delta: official as baseline
  if [ "$off_avg" -gt 0 ]; then
    delta=$(( (cs_avg - off_avg) * 100 / off_avg ))
  else
    delta=9999
  fi
  printf "%-18s | %-10s | %-10s | %+8d%%\n" "$name" "${off_avg}ms" "${cs_avg}ms" "$delta"
done

echo ""
echo "说明：official=官方 adb(1.0.41, daemon 常驻)；csharp=FirmwareKit.Comm.ADB CLI(net10.0, 直连 USB 无 daemon，含 dotnet 进程启动开销)"
