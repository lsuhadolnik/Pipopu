#!/bin/bash
set -euo pipefail

usage() {
    cat <<'USAGE'
Usage:
  scripts/debug-ios.sh --list
  scripts/debug-ios.sh --device DEVICE --team TEAM [--bundle-id ID] [--lldb]

DEVICE can be the name, CoreDevice identifier, or hardware UDID of a paired
iPhone or iPad connected over Wi-Fi. TEAM is your Apple development team ID.

The default mode installs Pipopu and streams its console. Use --lldb to launch
stopped and attach the debugger before the app begins running.
USAGE
}

device=""
team="${PIPOPU_TEAM_ID:-}"
bundle_id="${PIPOPU_BUNDLE_ID:-com.pipopu.app}"
mode=console
while [[ $# -gt 0 ]]; do
    case "$1" in
        --help|-h) usage; exit 0 ;;
        --list) exec xcrun devicectl list devices ;;
        --device|--team|--bundle-id)
            if [[ $# -lt 2 || "$2" == --* ]]; then usage >&2; exit 2; fi
            case "$1" in
                --device) device="$2" ;;
                --team) team="$2" ;;
                --bundle-id) bundle_id="$2" ;;
            esac
            shift 2 ;;
        --lldb) mode=lldb; shift ;;
        *) usage >&2; exit 2 ;;
    esac
done

if [[ -z "$device" || ! "$team" =~ ^[A-Z0-9]{10}$ || ! "$bundle_id" =~ ^[a-zA-Z0-9-]+(\.[a-zA-Z0-9-]+)+$ ]]; then
    usage >&2
    exit 2
fi

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
derived="$root/.build/ios-device"
device_json="$(mktemp -t pipopu-devices)"
trap 'rm -f "$device_json"' EXIT

xcrun devicectl list devices --quiet --json-output "$device_json"
ids="$(python3 - "$device" "$device_json" <<'PYTHON'
import json
import re
import sys

selector, path = sys.argv[1:]
with open(path) as stream:
    devices = json.load(stream)["result"]["devices"]

matches = []
for device in devices:
    hw = device.get("hardwareProperties", {})
    connection = device.get("connectionProperties", {})
    info = device.get("deviceProperties", {})
    core_id, udid = device.get("identifier", ""), hw.get("udid", "")
    if hw.get("platform") not in {"iOS", "iPadOS"} or hw.get("reality") == "simulated":
        continue
    if selector.casefold() in {core_id.casefold(), udid.casefold(), info.get("name", "").casefold()}:
        matches.append((core_id, udid, connection.get("transportType")))

if len(matches) != 1:
    sys.exit("Select one paired iPhone or iPad using --list and its identifier.")
core_id, udid, transport = matches[0]
if transport != "localNetwork":
    sys.exit("Connect the paired device over Wi-Fi and unplug its USB cable before running this script.")
if not all(re.fullmatch(r"[A-Fa-f0-9-]+", identifier) for identifier in (core_id, udid)):
    sys.exit("CoreDevice did not report both device identifiers.")
print(core_id, udid)
PYTHON
)"
read -r core_id udid <<< "$ids"

xcodebuild -project "$root/ios/Pipopu.xcodeproj" -scheme Pipopu \
    -configuration Debug -destination "id=$udid" -derivedDataPath "$derived" \
    -allowProvisioningUpdates -allowProvisioningDeviceRegistration \
    "DEVELOPMENT_TEAM=$team" "PIPOPU_BUNDLE_ID=$bundle_id" build

app="$derived/Build/Products/Debug-iphoneos/Pipopu.app"
xcrun devicectl device install app --device "$core_id" "$app"

if [[ "$mode" == lldb ]]; then
    xcrun devicectl device process launch --device "$core_id" --terminate-existing --start-stopped "$bundle_id"
    xcrun lldb "$app/Pipopu" -o "device select $core_id" -o "device process attach --name Pipopu"
else
    xcrun devicectl device process launch --device "$core_id" --terminate-existing --console "$bundle_id"
fi
