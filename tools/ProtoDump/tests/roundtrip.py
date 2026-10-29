"""以合成 schema 與選用的完整 descriptor 驗證 ProtoDump；不啟動 client 或 server。"""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path

from google.protobuf import any_pb2, descriptor_pb2, descriptor_pool, json_format, message_factory
from google.protobuf.internal.encoder import _VarintBytes


OPTIONS = r'''
syntax = "proto2";
package dump_fixture;
import "google/protobuf/descriptor.proto";
message Note { optional string text = 1; optional int32 count = 2; }
extend google.protobuf.FieldOptions {
  optional int32 marker = 61001;
  repeated sint32 markers = 61002 [packed = true];
  optional string field_note = 61003;
  optional Note annotation = 61004;
}
extend google.protobuf.MessageOptions { optional string message_note = 61005; }
extend google.protobuf.OneofOptions { optional string oneof_note = 61006; }
extend google.protobuf.EnumValueOptions { optional string value_note = 61007; }
'''
PROTO2 = r'''
syntax = "proto2";
package dump_fixture;
import public "options.proto";
option java_package = "fixture.tw";
option java_multiple_files = false;
option csharp_namespace = "Fixture.Taiwan";
enum State {
  option allow_alias = true;
  READY = 0 [(value_note) = "ready\n\"quoted\""];
  READY_ALIAS = 0;
  reserved -5 to -3;
  reserved "RETIRED_STATE";
}
message Holder {
  option (message_note) = "繁體中文\npath\\name";
  reserved 4 to 6;
  reserved "retired_name";
  extensions 100 to max;
  message Child { optional bytes payload = 1 [default = "\000\377\n"]; }
  optional string name = 1 [default = "line\n\"quote\"\\", (marker) = -7,
    (field_note) = "字串\t值", (annotation) = {text: "nested" count: 2}, deprecated = true];
  repeated int32 unpacked = 2 [packed = false, (markers) = -1, (markers) = 8];
  repeated int32 packed_values = 3 [packed = true];
  oneof choice {
    option (oneof_note) = "保留 oneof 選項";
    string chosen_text = 7 [(field_note) = "oneof field"];
    Child chosen_child = 8;
  }
  optional int32 oddly_named = 9 [json_name = "ExactJSON"];
}
extend Holder { optional string extra_text = 100 [default = "extended"]; }
service Example {
  option deprecated = true;
  rpc Read (Holder) returns (Holder) { option idempotency_level = NO_SIDE_EFFECTS; }
}
'''
PROTO3 = r'''
syntax = "proto3";
package dump_fixture3;
import "options.proto";
message Modern {
  optional int32 selected_count = 1;
  map<string, int32> counts = 2 [(dump_fixture.field_note) = "map outer field"];
  oneof selected { string text = 3 [(dump_fixture.marker) = 0]; int64 number = 4; }
}
'''


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def pool_for(ds):
    pool = descriptor_pool.DescriptorPool()
    provided = {f.name for f in ds.file}
    if descriptor_pb2.DESCRIPTOR.name not in provided:
        pool.AddSerializedFile(descriptor_pb2.DESCRIPTOR.serialized_pb)
    if any_pb2.DESCRIPTOR.name not in provided:
        pool.AddSerializedFile(any_pb2.DESCRIPTOR.serialized_pb)
    pending = list(ds.file)
    while pending:
        loaded = []
        for file in pending:
            try:
                pool.AddSerializedFile(file.SerializeToString())
                loaded.append(file.name)
            except Exception:
                pass
        if not loaded:
            raise ValueError("Unresolved descriptor dependencies: " + ", ".join(f.name for f in pending))
        pending = [f for f in pending if f.name not in loaded]
    return pool


def semantic(ds):
    pool = pool_for(ds)
    def options(value):
        cls = message_factory.GetMessageClass(pool.FindMessageTypeByName(value.DESCRIPTOR.full_name))
        decoded = cls.FromString(value.SerializeToString())
        clean = cls()
        clean.CopyFrom(decoded)
        clean.DiscardUnknownFields()
        if clean.SerializeToString(deterministic=True) != decoded.SerializeToString(deterministic=True):
            raise ValueError("Unknown option remained in " + value.DESCRIPTOR.full_name)
        return json_format.MessageToDict(decoded, preserving_proto_field_name=True, descriptor_pool=pool)

    def field(value, oneofs):
        return {"name": value.name, "number": value.number, "type": value.type, "type_name": value.type_name,
                "label": value.label, "extendee": value.extendee,
                "oneof": oneofs[value.oneof_index].name if value.HasField("oneof_index") else None,
                "proto3_optional": value.proto3_optional, "default": value.default_value if value.HasField("default_value") else None,
                "json_name": value.json_name, "options": options(value.options)}

    def enum(value):
        return {"name": value.name, "options": options(value.options),
                "values": [{"name": v.name, "number": v.number, "options": options(v.options)} for v in value.value],
                "reserved_ranges": [[r.start, r.end] for r in value.reserved_range], "reserved_names": list(value.reserved_name)}

    def message(value):
        return {"name": value.name, "options": options(value.options),
                "fields": [field(f, value.oneof_decl) for f in sorted(value.field, key=lambda f: f.number)],
                "extensions": [field(f, []) for f in sorted(value.extension, key=lambda f: f.number)],
                "oneofs": [{"name": o.name, "options": options(o.options)} for o in sorted(value.oneof_decl, key=lambda o: o.name)],
                # map syntax 會讓 protoc 重排合成的 nested entry；以完整名稱比對，不把宣告順序當 wire 身分。
                "nested": [message(m) for m in sorted(value.nested_type, key=lambda m: m.name)],
                "enums": [enum(e) for e in sorted(value.enum_type, key=lambda e: e.name)],
                "reserved_ranges": [[r.start, r.end] for r in value.reserved_range], "reserved_names": list(value.reserved_name),
                "extension_ranges": [{"start": r.start, "end": r.end, "options": options(r.options)} for r in value.extension_range]}

    return {f.name: {"package": f.package, "syntax": f.syntax or "proto2", "dependencies": list(f.dependency),
                     "public_dependencies": list(f.public_dependency), "weak_dependencies": list(f.weak_dependency),
                     "options": options(f.options), "messages": [message(m) for m in sorted(f.message_type, key=lambda m: m.name)],
                     "enums": [enum(e) for e in sorted(f.enum_type, key=lambda e: e.name)],
                     "extensions": [field(e, []) for e in sorted(f.extension, key=lambda e: e.number)],
                     "services": [{"name": s.name, "options": options(s.options),
                                   "methods": [{"name": m.name, "input": m.input_type, "output": m.output_type,
                                                "client_streaming": m.client_streaming, "server_streaming": m.server_streaming,
                                                "options": options(m.options)} for m in s.method]} for s in f.service]}
            for f in ds.file}


def differences(left, right, path=""):
    if type(left) is not type(right):
        return [{"path": path, "left": left, "right": right}]
    if isinstance(left, dict):
        result = []
        for key in sorted(set(left) | set(right)):
            if key not in left or key not in right:
                result.append({"path": path + "/" + key, "left": left.get(key), "right": right.get(key)})
            else:
                result += differences(left[key], right[key], path + "/" + key)
        return result
    if isinstance(left, list):
        if len(left) != len(right):
            return [{"path": path + "/length", "left": len(left), "right": len(right)}]
        return [v for i, (a, b) in enumerate(zip(left, right)) for v in differences(a, b, path + "/" + str(i))]
    return [] if left == right else [{"path": path, "left": left, "right": right}]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool", required=True, type=Path, help="已建置的 ProtoDump.dll")
    parser.add_argument("--protoc", required=True, type=Path)
    parser.add_argument("--include", required=True, type=Path, help="google/protobuf/*.proto 所在根目錄")
    parser.add_argument("--work", required=True, type=Path, help="獨立測試／LAB 輸出目錄")
    parser.add_argument("--descriptor", type=Path, help="選用：完整 client descriptor，僅讀取")
    args = parser.parse_args()
    work = args.work.resolve()
    if args.descriptor and args.descriptor.resolve().is_relative_to(work):
        raise ValueError("受保護 descriptor 不可位於測試輸出目錄")
    work.mkdir(parents=True, exist_ok=True)
    commands, cases = [], []
    def run(argv, success=True):
        result = subprocess.run([str(v) for v in argv], capture_output=True, text=True, encoding="utf-8", timeout=60, check=False)
        commands.append({"argv": [str(v) for v in argv], "exit_code": result.returncode, "stdout": result.stdout, "stderr": result.stderr})
        if success and result.returncode:
            raise RuntimeError(result.stderr)
        return result
    def compile_dir(folder, destination):
        run([args.protoc, "-I", folder, "-I", args.include, "--include_imports", "--descriptor_set_out=" + str(destination),
             *sorted(folder.glob("*.proto"))])
    def roundtrip(source, label):
        original_hash = sha(source)
        target = work / label
        run(["dotnet", args.tool, source, target])
        rebuilt = work / (label + ".pb")
        compile_dir(target, rebuilt)
        a = semantic(descriptor_pb2.FileDescriptorSet.FromString(source.read_bytes()))
        b = semantic(descriptor_pb2.FileDescriptorSet.FromString(rebuilt.read_bytes()))
        missing = sorted(set(a) - set(b))
        diff = differences(a, {k: b[k] for k in a if k in b})
        case = {"name": label, "source_sha256": original_hash, "source_unchanged": sha(source) == original_hash,
                "files": len(a), "additional_dependency_files": sorted(set(b) - set(a)), "missing_files": missing,
                "semantic_differences": diff, "status": "PASS" if not missing and not diff and sha(source) == original_hash else "FAIL"}
        cases.append(case)
        (work / (label + "-comparison.json")).write_text(json.dumps(case, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    fixtures = work / "fixtures"
    fixtures.mkdir(exist_ok=True)
    for name, content in (("options.proto", OPTIONS), ("holder.proto", PROTO2), ("modern.proto", PROTO3)):
        (fixtures / name).write_text(content.strip() + "\n", encoding="utf-8")
    fixture_descriptor = work / "fixture-input.pb"
    # 合成測試只輸入自己的 schema；相依 WKT 交由 pool 補入，避免把新版本 WKT 當成匯出器功能要求。
    run([args.protoc, "-I", fixtures, "-I", args.include, "--descriptor_set_out=" + str(fixture_descriptor),
         *sorted(fixtures.glob("*.proto"))])
    roundtrip(fixture_descriptor, "fixture-output")
    damaged = descriptor_pb2.FileDescriptorSet.FromString(fixture_descriptor.read_bytes())
    owner = next(f for f in damaged.file if f.name == "holder.proto")
    owner.message_type[0].field[0].options.MergeFromString(_VarintBytes(65000 << 3) + _VarintBytes(1))
    unknown = work / "unknown-option.pb"
    unknown.write_bytes(damaged.SerializeToString())
    target = work / "unknown-option-output"
    refused = run(["dotnet", args.tool, unknown, target], success=False)
    cases.append({"name": "unknown-option-fails-before-output", "status": "PASS" if refused.returncode != 0
                  and "Unknown option" in refused.stderr and not list(target.rglob("*.proto")) else "FAIL"})
    if args.descriptor:
        roundtrip(args.descriptor, "g-output")
    result = {"status": "PASS" if all(c["status"] == "PASS" for c in cases) else "FAIL", "cases": cases,
              "tools": {"ProtoDump_sha256": sha(args.tool), "protoc_sha256": sha(args.protoc)}, "commands": commands,
              "comparison": "wire fields, defaults/json_name, enum values, oneof/map, packed and all decoded options, reserved and extension declarations",
              "native_game_execution": "NOT_RUN", "server_execution": "NOT_RUN"}
    (work / "roundtrip-receipt.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "cases": [{"name": c["name"], "status": c["status"],
                      "differences": len(c.get("semantic_differences", []))} for c in cases]}, indent=2))
    if result["status"] != "PASS":
        raise SystemExit(1)


if __name__ == "__main__":
    main()
