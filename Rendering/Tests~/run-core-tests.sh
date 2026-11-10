#!/usr/bin/env bash
# Builds and runs the engine-free PVRP Core tests (Rendering/Core + Rendering/Tests/Editor/Core) without opening Unity,
# using the Roslyn compiler and the Mono runtime that ship inside the Unity Editor.
#
#   UNITY_EDITOR_DATA  Unity's Editor/Data folder   (default: D:/Unity/Editor/6000.4.11f1/Editor/Data)
#   NUNIT_DLL          nunit.framework.dll           (default: the project's Library/PackageCache copy)
#   OUT_DIR            build folder                  (default: $TMPDIR/pvrp-core-tests)
set -euo pipefail

winpath() { (cd "$1" && (pwd -W 2>/dev/null || pwd)); }

HERE="$(winpath "$(dirname "$0")")"
RENDERING="$(winpath "$HERE/..")"
PROJECT="$(winpath "$RENDERING/../../..")"
UNITY="${UNITY_EDITOR_DATA:-D:/Unity/Editor/6000.4.11f1/Editor/Data}"
API="$UNITY/MonoBleedingEdge/lib/mono/4.7.1-api"
OUT="${OUT_DIR:-${TMPDIR:-/tmp}/pvrp-core-tests}"
mkdir -p "$OUT"
OUT="$(winpath "$OUT")"

NUNIT="${NUNIT_DLL:-}"
if [ -z "$NUNIT" ]; then
  NUNIT="$(ls "$PROJECT"/Library/PackageCache/com.unity.ext.nunit@*/net40/unity-custom/nunit.framework.dll 2>/dev/null | head -1 || true)"
fi
if [ -z "$NUNIT" ] || [ ! -f "$NUNIT" ]; then
  echo "nunit.framework.dll not found; set NUNIT_DLL (it is in Library/PackageCache/com.unity.ext.nunit@*/net40/unity-custom/)." >&2
  exit 2
fi
cp "$NUNIT" "$OUT/nunit.framework.dll"

csc() {
  "$UNITY/NetCoreRuntime/dotnet.exe" exec "$UNITY/DotNetSdkRoslyn/csc.dll" -nologo -nostdlib -noconfig -langversion:9 -warn:4 \
    -r:"$API/mscorlib.dll" -r:"$API/System.dll" -r:"$API/System.Core.dll" "$@"
}

csc -target:library -out:"$OUT/ParelVR.Rendering.Core.dll" "$RENDERING"/Core/*.cs
csc -target:library -out:"$OUT/ParelVR.Rendering.Core.Tests.dll" -r:"$OUT/ParelVR.Rendering.Core.dll" -r:"$OUT/nunit.framework.dll" \
  "$RENDERING"/Tests/Editor/Core/*.cs
csc -target:exe -out:"$OUT/CoreTestRunner.exe" -r:"$OUT/nunit.framework.dll" "$HERE/CoreTestRunner.cs"

cd "$OUT"
"$UNITY/MonoBleedingEdge/bin/mono.exe" CoreTestRunner.exe ParelVR.Rendering.Core.Tests.dll
