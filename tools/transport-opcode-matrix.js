// Developers only: emit the hash-bound Phonest RPC method catalog recovered
// from the inspected client assemblies. These transport method indexes are a
// separate namespace from the game message opcodes in opcode-matrix.js.
//
// usage:
//   node tools/transport-opcode-matrix.js \
//     --phonest-proto <Phonest.Proto.dll> \
//     --phonest-client <Phonest.Client.dll> \
//     --phonest-common <Phonest.Common.dll> \
//     --out <output-directory>

'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const KNOWN_TRANSPORT_PROFILES = [{
  id: 'asia-genesis-client-2026-10-28',
  input_hashes: {
    phonest_proto_sha256: 'BEDF8FF6299D5641386EB80C80C7586CD5C898BC4F18A083B5C0C0D6C1644033',
    phonest_client_sha256: 'ACCBC1315E405F2E0DD333016ADE6FAABB05B7C792AB3D8B6DABDFCAC93315B3',
    phonest_common_sha256: '936AFD0B4D1C196E75E1C85A8FD44647B8FE00D21D577D1F0FAA8FE15947B212',
  },
  evidence: {
    phonest_proto_dispatch: 'ev_343b43ffab8ca77850fab66232d7051263425ab62cb3f8407aec0d4003e0b248',
    game_concrete_handler: 'ev_9904a30aa4177a540a9cf6365fd891c4fdd87fe1d6160cfa4b529f7b4aca4e2e',
    micro_stub_dispatch_and_handler: 'ev_75e8bccdea1a846f4d50d77fd95650c2a66eae0166a10b9c0d22918422c48492',
  },
}];

function parseArgs(argv) {
  const result = {};
  for (let i = 0; i < argv.length; i += 2) {
    const key = argv[i];
    if (!key || !key.startsWith('--') || argv[i + 1] === undefined) throw new Error(usage());
    result[key.slice(2)] = argv[i + 1];
  }
  if (!result['phonest-proto'] || !result['phonest-client'] || !result['phonest-common'] || !result.out) {
    throw new Error(usage());
  }
  return result;
}

function usage() {
  return 'usage: transport-opcode-matrix.js --phonest-proto <file> --phonest-client <file> --phonest-common <file> --out <dir>';
}

function sha256(file) {
  return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex').toUpperCase();
}

function inputProfileFor(hashes) {
  return KNOWN_TRANSPORT_PROFILES.find(profile => Object.entries(profile.input_hashes)
    .every(([key, value]) => hashes[key] === value)) || null;
}

function row(layer, direction, methodId, methodName, payloadType, definitionSource, concreteHandlerStatus, evidenceIds) {
  return {
    layer,
    direction,
    method_id: methodId,
    method_name: methodName,
    payload_type: payloadType,
    definition_source: definitionSource,
    concrete_handler_status: concreteHandlerStatus,
    runtime_observed: false,
    evidence_ids: evidenceIds,
  };
}

function gameClientToServer(evidence) {
  const proto = [evidence.phonest_proto_dispatch];
  const client = [evidence.phonest_proto_dispatch, evidence.game_concrete_handler];
  return [
    row('game_gate', 'client_to_server', 1, 'requestEncryptToken', 'Phonest.Proto.Void',
      'Phonest.Proto.IClientGateServiceStub.requestEncryptToken', 'generated_stub_implemented', proto),
    row('game_gate', 'client_to_server', 2, 'confirmEncryptKey', 'Phonest.Proto.EncryptKeyString',
      'Phonest.Proto.IClientGateServiceStub.confirmEncryptKey', 'generated_stub_implemented', proto),
    row('game_gate', 'client_to_server', 3, 'connectServer', 'Phonest.Proto.ConnectServerReq',
      'Phonest.Proto.IClientGateServiceStub.connectServer', 'generated_stub_implemented', proto),
    row('game_gate', 'client_to_server', 4, 'entityMessage', 'Phonest.Proto.EntityMessage',
      'Phonest.Client.Internal.GateClientHandler.CallRpcMessage', 'concrete_sender_implemented', client),
    row('game_gate', 'client_to_server', 5, 'channelMessage', 'Phonest.Proto.ChannelMessage',
      'Phonest.Proto.IClientGateServiceStub.channelMessage', 'generated_stub_implemented', proto),
  ];
}

function gameServerToClient(evidence) {
  const both = [evidence.phonest_proto_dispatch, evidence.game_concrete_handler];
  const definitions = [
    [1, 'connectResponse', 'ConnectServerResp', 'concrete_handler_implemented'],
    [2, 'entityMessage', 'EntityMessage', 'concrete_handler_implemented'],
    [3, 'channelMessage', 'ChannelMessage', 'concrete_handler_implemented'],
    [4, 'createChannelEntity', 'ChannelEntityInfo', 'concrete_handler_implemented'],
    [5, 'disconnectClient', 'DisconnectClientInfo', 'concrete_handler_implemented'],
    [6, 'bindAccount', 'BindAccountInfo', 'concrete_handler_implemented'],
    [7, 'bindAvatar', 'BindAvatarInfo', 'concrete_handler_implemented'],
    [8, 'bindSoul', 'BindSoulInfo', 'concrete_handler_implemented'],
    [9, 'unbindSoul', 'UnbindSoulInfo', 'concrete_handler_throws_not_implemented'],
    [10, 'responseEncryptToken', 'EncryptToken', 'concrete_handler_implemented'],
    [11, 'confirmEncryptKeyAck', 'Void', 'concrete_handler_implemented'],
    [12, 'recvHeartbeat', 'Heartbeat', 'inherited_base_throws_not_implemented'],
    [13, 'entityMessage2C', 'EntityMessage2C', 'inherited_base_throws_not_implemented'],
    [14, 'propertySync2Client', 'PropertySyncClientMessage', 'concrete_handler_implemented'],
    [15, 'propertyStrategySync2Client', 'PropertyStrategySyncClientMessage', 'inherited_base_throws_not_implemented'],
    [16, 'recoverChannelEntity', 'RecoverChannelEntityInfo', 'inherited_base_throws_not_implemented'],
    [17, 'createClientEntity', 'ClientEntityInfo', 'concrete_handler_implemented'],
    [18, 'propertyIdSync2Client', 'PropertyIdSyncClientMessage', 'inherited_base_throws_not_implemented'],
    [19, 'MicroServiceResponse', 'MsResponse', 'inherited_base_throws_not_implemented'],
    [20, 'MicroServicePush', 'MsPush', 'inherited_base_throws_not_implemented'],
  ];
  return definitions.map(([id, name, type, status]) => row(
    'game_gate', 'server_to_client', id, name, `Phonest.Proto.${type}`,
    `Phonest.Proto.IGateClientService<TStub>.DispatchProtobufRpc(${id})`, status, both));
}

function microClientToServer(evidence) {
  const common = [evidence.micro_stub_dispatch_and_handler];
  const type = 'Phonest.Common.Micro.Internal.IClientGateMicroServiceStub';
  return [
    row('micro_gate', 'client_to_server', 1, 'RequestEncryptToken', 'Phonest.Proto.Void',
      `${type}.RequestEncryptToken`, 'concrete_sender_implemented', common),
    row('micro_gate', 'client_to_server', 2, 'ConfirmEncryptKey', 'Phonest.Proto.EncryptKeyString',
      `${type}.ConfirmEncryptKey`, 'concrete_sender_implemented', common),
    row('micro_gate', 'client_to_server', 6, 'SendHeartbeat', 'Phonest.Proto.Heartbeat',
      `${type}.SendHeartbeat`, 'concrete_sender_implemented', common),
    row('micro_gate', 'client_to_server', 16, 'MicroReplyHeartbeat', 'Phonest.Proto.Heartbeat',
      `${type}.MicroReplyHeartbeat`, 'concrete_sender_implemented', common),
    row('micro_gate', 'client_to_server', 33, 'MicroServiceRequest', 'Phonest.Proto.MsRequest',
      `${type}.MicroServiceRequest`, 'concrete_sender_implemented', common),
  ];
}

function microServerToClient(evidence) {
  const common = [evidence.micro_stub_dispatch_and_handler];
  const type = 'Phonest.Common.Micro.Internal.IGateClientMicroService<TStub>.DispatchProtobufRpc';
  return [
    row('micro_gate', 'server_to_client', 5, 'MICRO_REG_GATE_SERVER', null,
      `${type}(5)`, 'accepted_ignored_payload_unparsed', common),
    row('micro_gate', 'server_to_client', 8, 'MicroHeartbeat', 'Phonest.Proto.Heartbeat',
      `${type}(8)`, 'concrete_handler_implemented', common),
    row('micro_gate', 'server_to_client', 10, 'ResponseEncryptToken', 'Phonest.Proto.EncryptToken',
      `${type}(10)`, 'concrete_handler_implemented', common),
    row('micro_gate', 'server_to_client', 11, 'ConfirmEncryptKeyAck', 'Phonest.Proto.Void',
      `${type}(11)`, 'concrete_handler_implemented', common),
    row('micro_gate', 'server_to_client', 12, 'MSG_RESPONSE_HEART_BEAT', null,
      `${type}(12)`, 'accepted_ignored_payload_unparsed', common),
    row('micro_gate', 'server_to_client', 34, 'MicroServiceResponse', 'Phonest.Proto.MsResponse',
      `${type}(34)`, 'concrete_handler_implemented', common),
    row('micro_gate', 'server_to_client', 35, 'MicroServicePush', 'Phonest.Proto.MsPush',
      `${type}(35)`, 'concrete_handler_implemented', common),
  ];
}

function countBy(rows, key) {
  const counts = {};
  for (const item of rows) counts[item[key]] = (counts[item[key]] || 0) + 1;
  return counts;
}

function buildTransportMatrix(inputHashes) {
  const profile = inputProfileFor(inputHashes);
  if (!profile) {
    throw new Error('unsupported transport assembly hashes; refusing to apply version-specific RPC semantics');
  }
  const rows = [
    ...gameClientToServer(profile.evidence),
    ...gameServerToClient(profile.evidence),
    ...microClientToServer(profile.evidence),
    ...microServerToClient(profile.evidence),
  ];
  const byLayerDirection = {};
  for (const item of rows) {
    const key = `${item.layer}:${item.direction}`;
    byLayerDirection[key] = (byLayerDirection[key] || 0) + 1;
  }
  return {
    rows,
    summary: {
      static_only: true,
      runtime_capture_complete: false,
      input_profile: profile.id,
      input_hashes: inputHashes,
      evidence: profile.evidence,
      counts: {
        transport_method_rows: rows.length,
        by_layer: countBy(rows, 'layer'),
        by_direction: countBy(rows, 'direction'),
        by_layer_direction: byLayerDirection,
        by_concrete_handler_status: countBy(rows, 'concrete_handler_status'),
      },
      namespace_boundary: 'Phonest RPC method indexes are not game message opcodes and must not be merged by number.',
      unknowns: [
        'No live packet capture was performed; runtime_observed is false for every row.',
        'Micro gate methods 5 and 12 accept and ignore the body without parsing it, so their payload types remain UNKNOWN.',
        'Game gate methods 19 and 20 are decoded by the generic base but the concrete GateClientHandler inherits throwing implementations.',
      ],
    },
  };
}

function tsvCell(value) {
  if (value === null || value === undefined) return '';
  const text = Array.isArray(value) ? value.join(',') : String(value);
  return text.replaceAll('\t', '\\t').replaceAll('\r', '\\r').replaceAll('\n', '\\n');
}

function emit(result, outDir) {
  fs.mkdirSync(outDir, { recursive: true });
  fs.writeFileSync(path.join(outDir, 'transport-method-matrix.json'), `${JSON.stringify(result.rows, null, 2)}\n`);
  const columns = [
    'layer', 'direction', 'method_id', 'method_name', 'payload_type', 'definition_source',
    'concrete_handler_status', 'runtime_observed', 'evidence_ids',
  ];
  const lines = [columns.join('\t'), ...result.rows.map(item => columns.map(key => tsvCell(item[key])).join('\t'))];
  fs.writeFileSync(path.join(outDir, 'transport-method-matrix.tsv'), `${lines.join('\n')}\n`);
  fs.writeFileSync(path.join(outDir, 'transport-summary.json'), `${JSON.stringify(result.summary, null, 2)}\n`);
}

if (require.main === module) {
  try {
    const options = parseArgs(process.argv.slice(2));
    const inputHashes = {
      phonest_proto_sha256: sha256(options['phonest-proto']),
      phonest_client_sha256: sha256(options['phonest-client']),
      phonest_common_sha256: sha256(options['phonest-common']),
    };
    const result = buildTransportMatrix(inputHashes);
    emit(result, options.out);
    console.log(`${result.rows.length} transport method rows -> ${options.out}`);
    console.log(JSON.stringify(result.summary.counts, null, 2));
  } catch (error) {
    console.error(error.stack || error.message || String(error));
    process.exitCode = 1;
  }
}

module.exports = {
  KNOWN_TRANSPORT_PROFILES,
  buildTransportMatrix,
  emit,
  inputProfileFor,
};
