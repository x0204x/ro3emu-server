'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const {
  KNOWN_INPUT_PROFILES,
  directionFor,
  inputProfileFor,
  parseAutoSyncBridge,
  parseAutoSyncConsumers,
  parseAssetDatasetConsumers,
  parseDescriptorSet,
  parseSchemaFile,
} = require('../opcode-matrix.js');

function varint(input) {
  let value = BigInt(input);
  const bytes = [];
  do {
    let byte = Number(value & 0x7fn);
    value >>= 7n;
    if (value) byte |= 0x80;
    bytes.push(byte);
  } while (value);
  return Buffer.from(bytes);
}

function fieldVarint(number, value) {
  return Buffer.concat([varint((number << 3) | 0), varint(value)]);
}

function fieldBytes(number, value) {
  const bytes = Buffer.isBuffer(value) ? value : Buffer.from(value);
  return Buffer.concat([varint((number << 3) | 2), varint(bytes.length), bytes]);
}

test('direction classification keeps request, notify, oneway and null/null distinct', () => {
  assert.equal(directionFor({ request_type: 'romsg.Req', response_type: 'romsg.Res' }), 'client_request_server_response');
  assert.equal(directionFor({ request_type: 'romsg.NullArg', response_type: 'romsg.Ntf' }), 'server_to_client_notify');
  assert.equal(directionFor({ request_type: 'romsg.null', response_type: 'romsg.Ntf' }), 'server_to_client_notify');
  assert.equal(directionFor({ request_type: 'ezmsg.NullArg', response_type: 'ezmsg.Ntf' }), 'server_to_client_notify');
  assert.equal(directionFor({ request_type: 'romsg.Req', response_type: 'romsg.NullArg' }), 'client_to_server_oneway');
  assert.equal(directionFor({ request_type: 'romsg.Req', response_type: 'romsg.null' }), 'client_to_server_oneway');
  assert.equal(directionFor({ request_type: 'ezmsg.Req', response_type: 'ezmsg.NullArg' }), 'client_to_server_oneway');
  assert.equal(directionFor({ request_type: 'romsg.NullArg', response_type: 'romsg.NullArg' }), 'null_null');
});

test('decompiled Lua schema parser preserves id, types, msg_type and source line', t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ro3-opcode-schema-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, 'schema.lua');
  fs.writeFileSync(file, [
    'L1_1 = 5001',
    'L2_1 = {}',
    'L2_1.msg_name = "AUTO_SYNC_TEST"',
    'L2_1.msg_id = L1_1',
    'L2_1.req_name = "romsg.NullArg"',
    'L2_1.res_name = "romsg.TestIncrement"',
    'L2_1.msg_type = 1',
    'L0_1[L1_1] = L2_1',
  ].join('\n'));
  const rows = parseSchemaFile(file, 'Network/schema.lua', 'autosync');
  assert.deepEqual(rows.map(row => ({
    id: row.id,
    name: row.name,
    req: row.request_type,
    res: row.response_type,
    type: row.message_type,
    line: row.source.line,
  })), [{
    id: 5001,
    name: 'AUTO_SYNC_TEST',
    req: 'romsg.NullArg',
    res: 'romsg.TestIncrement',
    type: 1,
    line: 3,
  }]);
});

test('FileDescriptorSet parser recovers package messages and MSG_Type enum', t => {
  const enumValue = Buffer.concat([fieldBytes(1, 'TT_TEST'), fieldVarint(2, 123)]);
  const enumDescriptor = Buffer.concat([fieldBytes(1, 'MSG_Type'), fieldBytes(2, enumValue)]);
  const payloadField = Buffer.concat([
    fieldBytes(1, 'types'),
    fieldVarint(3, 1),
    fieldVarint(4, 3), // LABEL_REPEATED
    fieldVarint(5, 14), // TYPE_ENUM
    fieldBytes(6, '.romsg.MSG_Type'),
    fieldBytes(10, 'types'),
  ]);
  const messageDescriptor = Buffer.concat([fieldBytes(1, 'TestPayload'), fieldBytes(2, payloadField)]);
  const fileDescriptor = Buffer.concat([
    fieldBytes(1, 'MG_Define.proto'),
    fieldBytes(2, 'romsg'),
    fieldBytes(4, messageDescriptor),
    fieldBytes(5, enumDescriptor),
    fieldBytes(12, 'proto3'),
  ]);
  const set = fieldBytes(1, fileDescriptor);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ro3-opcode-descriptor-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, 'proto.pb.bytes');
  fs.writeFileSync(file, set);
  const parsed = parseDescriptorSet(file);
  assert.equal(parsed.files.length, 1);
  assert.equal(parsed.topLevelMessageCount, 1);
  assert.equal(parsed.messages.get('romsg.TestPayload'), 'MG_Define.proto');
  assert.deepEqual(parsed.enums.find(item => item.full_name === 'romsg.MSG_Type').values, [{ name: 'TT_TEST', number: 123 }]);
  assert.deepEqual(parsed.messageSchemas.get('romsg.TestPayload'), {
    full_name: 'romsg.TestPayload',
    file: 'MG_Define.proto',
    syntax: 'proto3',
    fields: [{
      name: 'types',
      number: 1,
      label: 'repeated',
      type: 'enum',
      type_name: 'romsg.MSG_Type',
      wire_type: 'varint_or_packed_length_delimited',
      default_value: null,
      oneof_index: null,
      json_name: 'types',
      proto3_optional: false,
      packed_option: null,
      effective_packed: true,
    }],
    oneofs: [],
    map_entry: false,
  });
});

test('AutoSync data definition parser links message names to DAL consumers', t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ro3-opcode-autosync-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, 'autosync.lua');
  fs.writeFileSync(file, [
    'L0_1 = {}',
    'L1_1 = {}',
    'L2_1 = {}',
    'L2_1.DAL = "lc_dal_test"',
    'L1_1.TestInfo = L2_1',
    'L0_1.AUTO_SYNC_CLIENT_TEST = L1_1',
  ].join('\n'));
  const parsed = parseAutoSyncConsumers(file);
  assert.deepEqual(parsed.get('AUTO_SYNC_CLIENT_TEST').consumers, [{
    field: 'TestInfo',
    dal: 'lc_dal_test',
    custom_param: null,
    line: 5,
  }]);
});

test('asset dataset parser handles direct and register-indexed consumer definitions', t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ro3-opcode-dataset-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, 'dataset.lua');
  fs.writeFileSync(file, [
    'L0_1 = {}',
    'L1_1 = {}',
    'L2_1 = {}',
    'L2_1.DAL = "lc_dal_direct"',
    'L2_1.PBFiled = "payload"',
    'L1_1.DirectConsumer = L2_1',
    'L0_1.DIRECT_MESSAGE = L1_1',
    'L1_1 = "INDEXED_MESSAGE"',
    'L2_1 = {}',
    'L3_1 = "IndexedConsumer"',
    'L4_1 = {}',
    'L5_1 = "lc_dal_indexed"',
    'L4_1.DAL = L5_1',
    'L6_1 = "IndexedParam"',
    'L4_1.CustomParam = L6_1',
    'L7_1 = "body"',
    'L4_1.PBFiled = L7_1',
    'L2_1[L3_1] = L4_1',
    'L0_1[L1_1] = L2_1',
  ].join('\n'));
  const parsed = parseAssetDatasetConsumers(file);
  assert.deepEqual(parsed.get('DIRECT_MESSAGE').consumers, [{
    field: 'DirectConsumer',
    dal: 'lc_dal_direct',
    custom_param: null,
    line: 6,
    pb_field: 'payload',
  }]);
  assert.deepEqual(parsed.get('INDEXED_MESSAGE').consumers, [{
    field: 'IndexedConsumer',
    dal: 'lc_dal_indexed',
    custom_param: 'IndexedParam',
    line: 18,
    pb_field: 'body',
  }]);
});

test('version-specific evidence is selected only for an exact input hash profile', () => {
  const hashes = structuredClone(KNOWN_INPUT_PROFILES[0].input_hashes);
  assert.equal(inputProfileFor(hashes)?.id, 'asia-client-2026-10-28');

  hashes.callback_lua_sha256 = '0'.repeat(64);
  assert.equal(inputProfileFor(hashes), null);

  const datasetHashes = structuredClone(KNOWN_INPUT_PROFILES[0].input_hashes);
  datasetHashes.asset_dataset_lua_sha256 = '0'.repeat(64);
  assert.equal(inputProfileFor(datasetHashes), null);
});

test('AutoSync bridge line numbers are derived from the supplied Lua file', t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ro3-opcode-bridge-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, 'asset-manager.lua');
  fs.writeFileSync(file, [
    'L0_1 = {}',
    'L0_1.AutoSync = L6_1',
    '-- a version-specific extra line',
    'L0_1.PreDecode = L9_1',
  ].join('\n'));

  assert.deepEqual(parseAutoSyncBridge(file), {
    file: 'Logic/Asset/LC_AssetManager.lua',
    pre_decode_line: 4,
    auto_sync_line: 2,
  });
});
