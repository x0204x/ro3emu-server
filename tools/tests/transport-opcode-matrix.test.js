'use strict';

const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const {
  KNOWN_TRANSPORT_PROFILES,
  buildTransportMatrix,
  emit,
  inputProfileFor,
} = require('../transport-opcode-matrix.js');

function knownHashes() {
  return structuredClone(KNOWN_TRANSPORT_PROFILES[0].input_hashes);
}

function keyed(rows, layer, direction, method) {
  return rows.find(row => row.layer === layer && row.direction === direction && row.method_id === method);
}

test('transport semantics require all three exact assembly hashes', () => {
  const hashes = knownHashes();
  assert.equal(inputProfileFor(hashes)?.id, 'asia-genesis-client-2026-10-28');
  hashes.phonest_common_sha256 = '0'.repeat(64);
  assert.equal(inputProfileFor(hashes), null);
  assert.throws(() => buildTransportMatrix(hashes), /unsupported transport assembly hashes/);
});

test('catalog contains the complete 37-row layer and direction partition', () => {
  const result = buildTransportMatrix(knownHashes());
  assert.equal(result.rows.length, 37);
  assert.deepEqual(result.summary.counts.by_layer_direction, {
    'game_gate:client_to_server': 5,
    'game_gate:server_to_client': 20,
    'micro_gate:client_to_server': 5,
    'micro_gate:server_to_client': 7,
  });
  assert.equal(result.summary.runtime_capture_complete, false);
});

test('compound method keys are unique while equal numbers remain separate across layers and directions', () => {
  const rows = buildTransportMatrix(knownHashes()).rows;
  const keys = rows.map(row => `${row.layer}:${row.direction}:${row.method_id}`);
  assert.equal(new Set(keys).size, rows.length);
  assert.equal(rows.filter(row => row.method_id === 1).length, 3);
});

test('game gate method and payload sets match the recovered stubs and dispatch switch', () => {
  const rows = buildTransportMatrix(knownHashes()).rows;
  const c2s = rows.filter(row => row.layer === 'game_gate' && row.direction === 'client_to_server');
  const s2c = rows.filter(row => row.layer === 'game_gate' && row.direction === 'server_to_client');
  assert.deepEqual(c2s.map(row => [row.method_id, row.payload_type]), [
    [1, 'Phonest.Proto.Void'],
    [2, 'Phonest.Proto.EncryptKeyString'],
    [3, 'Phonest.Proto.ConnectServerReq'],
    [4, 'Phonest.Proto.EntityMessage'],
    [5, 'Phonest.Proto.ChannelMessage'],
  ]);
  assert.deepEqual(s2c.map(row => row.method_id), Array.from({ length: 20 }, (_, index) => index + 1));
});

test('game concrete handler failures stay explicit and cannot be promoted to supported sends', () => {
  const rows = buildTransportMatrix(knownHashes()).rows;
  assert.equal(keyed(rows, 'game_gate', 'server_to_client', 9).concrete_handler_status,
    'concrete_handler_throws_not_implemented');
  for (const method of [12, 13, 15, 16, 18, 19, 20]) {
    assert.equal(keyed(rows, 'game_gate', 'server_to_client', method).concrete_handler_status,
      'inherited_base_throws_not_implemented');
  }
  assert.equal(keyed(rows, 'game_gate', 'server_to_client', 19).payload_type, 'Phonest.Proto.MsResponse');
  assert.equal(keyed(rows, 'game_gate', 'server_to_client', 20).payload_type, 'Phonest.Proto.MsPush');
});

test('micro response and push are implemented while ignored methods retain unknown payloads', () => {
  const rows = buildTransportMatrix(knownHashes()).rows;
  for (const method of [34, 35]) {
    assert.equal(keyed(rows, 'micro_gate', 'server_to_client', method).concrete_handler_status,
      'concrete_handler_implemented');
  }
  for (const method of [5, 12]) {
    const item = keyed(rows, 'micro_gate', 'server_to_client', method);
    assert.equal(item.concrete_handler_status, 'accepted_ignored_payload_unparsed');
    assert.equal(item.payload_type, null);
  }
});

test('emission is byte deterministic', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'ro3-transport-matrix-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const first = path.join(root, 'first');
  const second = path.join(root, 'second');
  const result = buildTransportMatrix(knownHashes());
  emit(result, first);
  emit(result, second);
  for (const name of ['transport-method-matrix.json', 'transport-method-matrix.tsv', 'transport-summary.json']) {
    const digest = directory => crypto.createHash('sha256').update(fs.readFileSync(path.join(directory, name))).digest('hex');
    assert.equal(digest(first), digest(second));
  }
});
