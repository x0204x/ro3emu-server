// Developers only: build a static, reproducible opcode registry matrix from the
// decompiled client Lua and the shipped FileDescriptorSet. This does not execute
// client code and does not establish runtime packet traffic.
//
// usage:
//   node tools/opcode-matrix.js --lua <decompiled-lua-root> \
//     --descriptor <proto.pb.bytes> --out <output-directory>

'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const SCHEMA_FILES = [
  ['Network/NW_Helper_PBDefine.lua', 'protobuf'],
  ['Network/NW_Helper_EZPBDefine.lua', 'ezprotobuf'],
  ['Network/NW_AutoSyncClient_Define.lua', 'autosync'],
];

const NULL_ARG_TYPES = new Set(['romsg.NullArg', 'romsg.null', 'ezmsg.NullArg']);

// Version-specific interpretation is fail-closed.  Generic descriptor/Lua
// decoding remains available for other builds, but evidence IDs and semantic
// overrides are emitted only when every relevant input hash matches.
const KNOWN_INPUT_PROFILES = [{
  id: 'asia-client-2026-10-28',
  input_hashes: {
    descriptor_sha256: 'B5CF437794324A834854115CF5F69FF1A738E79E16228C0D42BD9D95C02DEA00',
    lua_registry_sha256: {
      'Network/NW_Helper_PBDefine.lua': 'C00658CDE38147459F3D1B437D05854E28ECC1CE1CAAAB87005215F92239092C',
      'Network/NW_Helper_EZPBDefine.lua': 'D963C3221E7FAA128E99B66805C8A0F683F217777B6990954E2DE295F7162267',
      'Network/NW_AutoSyncClient_Define.lua': 'C87FEC77E075C6A1121B82C389932F79B6A56B72DCAC933560E5DD6C6429404C',
    },
    callback_lua_sha256: '04C1F6A93A3E264E1B744548CB826C7E2700B3A09E57DA46D89AE073B3AE4267',
    scene_consumer_lua_sha256: '1702FA646CFD32C668A605AF21C3BCB0A50B1DE799C99B4BBAF0A5AFFD28F108',
    base_handler_lua_sha256: '1E79406199B8BBEB0026606EAD4EDDFE8E54BF7E5BD822AB66FC5488984EAE3A',
    autosync_consumer_lua_sha256: 'A554AAB7221051527B93FB0A1FD59AD92E03BCA2EE0C44FBEB9BD82EF66A0EAA',
    asset_manager_lua_sha256: 'B143168287EDC5569B80D1819FD73F248CD3E5A8B3276EABBFAE0BB2B8D8CEEF',
    asset_dataset_lua_sha256: '7B5BC575E22239833464B4B529B8FA63898C1233CCA98AE1259FD52D5D0EEC80',
  },
  managed_bridge: 'HappyEngineClient.OnMsgReceived -> NW_CBBNetworkManager -> Lua HandlerArray',
  managed_bridge_evidence: {
    managed_artifact_evidence_id: 'ev_98890a6a96966cf5c7e665aed057a4234fa0c8475922b60e4a48cde03440ba3e',
    nw_cbbnetworkmanager_evidence_id: 'ev_bd855b2980c5745f9ef6fe22dd6574ba88918228c0b0cbbc0d350dccb7d8fe53',
    scope: 'global inbound bridge; no per-opcode Managed registry was established',
  },
  descriptor_only: {
    1008: {
      registry_class: 'stale_or_external_unknown',
      descriptor_message_candidates: ['romsg.AccountDeleteRoleReq', 'romsg.AccountDeleteRoleResp'],
    },
    77010: {
      registry_class: 'official_managed_chat_descriptor_only',
      descriptor_message_candidates: ['romsg.ManagedChatBootstrapReq', 'romsg.ManagedChatBootstrapRsp'],
    },
    77011: {
      registry_class: 'official_managed_chat_descriptor_only',
      descriptor_message_candidates: ['romsg.ManagedChatCredentialRefreshReq', 'romsg.ManagedChatCredentialRefreshRsp'],
    },
    77012: {
      registry_class: 'official_managed_chat_descriptor_only',
      descriptor_message_candidates: ['romsg.ManagedChatSendReq', 'romsg.ManagedChatSendRsp'],
    },
    77013: {
      registry_class: 'official_managed_chat_descriptor_only',
      descriptor_message_candidates: ['romsg.ManagedChatChannelStateNtf'],
    },
    84072: {
      registry_class: 'paired_response_enum',
      descriptor_message_candidates: ['romsg.DragonIslandClaimRankRewardRes'],
      paired_schema_opcode: 84071,
    },
  },
}];

function inputProfileFor(hashes) {
  const canonical = value => JSON.stringify(value, Object.keys(value).sort());
  return KNOWN_INPUT_PROFILES.find(profile => {
    const expected = profile.input_hashes;
    return expected.descriptor_sha256 === hashes.descriptor_sha256
      && canonical(expected.lua_registry_sha256) === canonical(hashes.lua_registry_sha256)
      && expected.callback_lua_sha256 === hashes.callback_lua_sha256
      && expected.scene_consumer_lua_sha256 === hashes.scene_consumer_lua_sha256
      && expected.base_handler_lua_sha256 === hashes.base_handler_lua_sha256
      && expected.autosync_consumer_lua_sha256 === hashes.autosync_consumer_lua_sha256
      && expected.asset_manager_lua_sha256 === hashes.asset_manager_lua_sha256
      && expected.asset_dataset_lua_sha256 === hashes.asset_dataset_lua_sha256;
  }) || null;
}

function parseArgs(argv) {
  const result = {};
  for (let i = 0; i < argv.length; i += 2) {
    const key = argv[i];
    if (!key || !key.startsWith('--') || argv[i + 1] === undefined) {
      throw new Error('usage: opcode-matrix.js --lua <dir> --descriptor <file> --out <dir>');
    }
    result[key.slice(2)] = argv[i + 1];
  }
  if (!result.lua || !result.descriptor || !result.out) {
    throw new Error('usage: opcode-matrix.js --lua <dir> --descriptor <file> --out <dir>');
  }
  return result;
}

function sha256(file) {
  return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex').toUpperCase();
}

function lineArray(file) {
  return fs.readFileSync(file, 'utf8').split(/\r?\n/);
}

function scalar(value, registers) {
  if (registers.has(value)) return registers.get(value);
  if (/^".*"$/.test(value)) return value.slice(1, -1);
  if (/^-?\d+$/.test(value)) return Number(value);
  return value;
}

function parseSchemaFile(file, logicalFile, family) {
  const lines = lineArray(file);
  const registers = new Map();
  const rows = [];
  let current = null;

  function finish(endLine) {
    if (!current) return;
    current.end_line = endLine;
    current.source = { file: logicalFile, line: current.start_line, family };
    rows.push(current);
    current = null;
  }

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    let match = line.match(/^\s*(L\d+_\d+)\s*=\s*("[^"]*"|-?\d+)\s*$/);
    if (match) {
      registers.set(match[1], scalar(match[2], registers));
      continue;
    }
    match = line.match(/\.(msg_name|msg_id|req_name|res_name|msg_type)\s*=\s*(\S+)\s*$/);
    if (!match) continue;
    const value = scalar(match[2], registers);
    if (match[1] === 'msg_name') {
      finish(index);
      current = { name: String(value), start_line: index + 1 };
    } else if (current) {
      const key = {
        msg_id: 'id',
        req_name: 'request_type',
        res_name: 'response_type',
        msg_type: 'message_type',
      }[match[1]];
      current[key] = value;
    }
  }
  finish(lines.length);
  return rows;
}

function schemaShape(row) {
  const req = row.request_type;
  const res = row.response_type;
  if (!req || !res) return 'unknown';
  const reqNull = NULL_ARG_TYPES.has(req);
  const resNull = NULL_ARG_TYPES.has(res);
  if (reqNull && resNull) return 'empty_both';
  if (reqNull) return 'empty_request';
  if (resNull) return 'empty_response';
  return 'nonempty_pair';
}

function observedRoleFor(row) {
  const clientSender = Array.isArray(row.client_senders) && row.client_senders.length > 0;
  const serverConsumer = Boolean(row.callback || row.scene_consumer);
  if (clientSender && serverConsumer) return 'both';
  if (clientSender) return 'client_sender';
  if (serverConsumer) return 'server_consumer';
  return 'unknown';
}

function directionFor(row, observedRole = observedRoleFor(row)) {
  const shape = schemaShape(row);
  if (observedRole === 'client_sender' || observedRole === 'both') {
    if (shape === 'empty_response' || shape === 'empty_both') return 'client_to_server_oneway';
    if (shape === 'nonempty_pair' || shape === 'empty_request') return 'client_request_server_response';
    return 'client_to_server_unknown_reply_shape';
  }
  if (observedRole === 'server_consumer') return 'server_to_client_observed';
  return 'unknown';
}

function findBound(lines, startPattern, endPattern) {
  const startAnchor = lines.findIndex(line => startPattern.test(line));
  if (startAnchor < 0) return null;
  const endOffset = lines.slice(startAnchor + 1).findIndex(line => endPattern.test(line));
  if (endOffset < 0) return null;
  return [startAnchor + 1, startAnchor + 1 + endOffset];
}

function parseNameHandlerMappings(file, logicalFile, bound) {
  const lines = lineArray(file);
  const [start, end] = bound(lines) || [0, -1];
  const rows = [];
  for (let i = start; i <= end - 2; i++) {
    const map = lines[i].match(/^\s*(L\d+_\d+)\s*=\s*\1\.ProtobufNameMap\s*$/);
    if (!map) continue;
    const name = lines[i + 1]?.match(new RegExp(`^\\s*${map[1]}\\s*=\\s*${map[1]}\\.([A-Za-z0-9_]+)\\s*$`));
    const handler = lines[i + 2]?.match(/^\s*L\d+_\d+\s*=\s*A0_2\.([A-Za-z0-9_]+)\s*$/);
    if (name && handler) rows.push({ name: name[1], handler: handler[1], file: logicalFile, line: i + 2 });
  }
  return rows;
}

function gameCallbackBound(lines) {
  return findBound(
    lines,
    /^L1_1\._InitPb = L7_1\s*$/,
    /^L1_1\._InitCallBackFunc = L7_1\s*$/,
  );
}

function sceneConsumerBound(lines) {
  const end = lines.findIndex(line => /^\s*A0_2\.KVMsgFunc = L1_2\s*$/.test(line));
  if (end < 0) return null;
  let start = end;
  while (start >= 0 && !/^\s*L1_2 = \{\}\s*$/.test(lines[start])) start--;
  return start >= 0 ? [start, end] : null;
}

function parseNameValueTable(lines, finalPattern) {
  const end = lines.findIndex(line => finalPattern.test(line));
  if (end < 0) return [];
  let start = end;
  while (start >= 0 && !/^\s*L1_2 = \{\}\s*$/.test(lines[start])) start--;
  if (start < 0) return [];
  const result = [];
  for (let i = start; i < end - 1; i++) {
    const map = lines[i].match(/^\s*(L\d+_\d+)\s*=\s*\1\.ProtobufNameMap\s*$/);
    if (!map) continue;
    const name = lines[i + 1]?.match(new RegExp(`^\\s*${map[1]}\\s*=\\s*${map[1]}\\.([A-Za-z0-9_]+)\\s*$`));
    if (!name) continue;
    let value = null;
    for (let j = i + 2; j < Math.min(end, i + 9); j++) {
      if (j > i + 2 && /\.ProtobufNameMap\s*$/.test(lines[j])) break;
      const assignment = lines[j].match(/^\s*L1_2\[L2_2\]\s*=\s*(true|-?\d+|L3_2)\s*$/);
      if (assignment) {
        value = assignment[1] === 'true' ? true : assignment[1] === 'L3_2' ? 'counter' : Number(assignment[1]);
        break;
      }
    }
    if (value !== null) result.push({ name: name[1], value, line: i + 2 });
  }
  return result;
}

function parseBaseRoutes(file) {
  const lines = lineArray(file);
  return {
    queued_before_dispatch: parseNameValueTable(lines, /^\s*L15_1 = L1_2\s*$/),
    decode_turn: parseNameValueTable(lines, /^\s*L7_1 = L1_2\s*$/),
    no_decode: parseNameValueTable(lines, /^\s*L8_1 = L1_2\s*$/),
    bounded_counter: parseNameValueTable(lines, /^\s*L9_1 = L1_2\s*$/),
  };
}

function parseDatasetConsumers(file, logicalFile, includeName) {
  const lines = lineArray(file);
  const registers = new Map();
  registers.set('L0_1', { properties: new Map() });
  const object = line => ({ properties: new Map(), line });

  function valueOf(token) {
    if (registers.has(token)) return registers.get(token);
    if (/^".*"$/.test(token)) return token.slice(1, -1);
    if (/^-?\d+$/.test(token)) return Number(token);
    return token;
  }

  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    let match = line.match(/^\s*(L\d+_\d+)\s*=\s*\{\}\s*$/);
    if (match) {
      registers.set(match[1], object(index + 1));
      continue;
    }
    match = line.match(/^\s*(L\d+_\d+)\s*=\s*("[^"]*"|-?\d+)\s*$/);
    if (match) {
      registers.set(match[1], valueOf(match[2]));
      continue;
    }
    match = line.match(/^\s*(L\d+_\d+)\.([A-Za-z0-9_]+)\s*=\s*(L\d+_\d+|"[^"]*"|-?\d+)\s*$/);
    if (match) {
      const target = registers.get(match[1]);
      if (target?.properties) target.properties.set(match[2], { value: valueOf(match[3]), line: index + 1 });
      continue;
    }
    match = line.match(/^\s*(L\d+_\d+)\[(L\d+_\d+)\]\s*=\s*(L\d+_\d+|"[^"]*"|-?\d+)\s*$/);
    if (match) {
      const target = registers.get(match[1]);
      const key = valueOf(match[2]);
      if (target?.properties && typeof key === 'string') target.properties.set(key, { value: valueOf(match[3]), line: index + 1 });
    }
  }

  const root = registers.get('L0_1');
  const result = new Map();
  for (const [name, entry] of root.properties) {
    if (!includeName(name) || !entry.value?.properties) continue;
    const consumers = [];
    for (const [field, fieldEntry] of entry.value.properties) {
      const dal = fieldEntry.value?.properties?.get('DAL')?.value;
      if (typeof dal !== 'string') continue;
      const customParam = fieldEntry.value.properties.get('CustomParam')?.value ?? null;
      const pbField = fieldEntry.value.properties.get('PBFiled')?.value ?? null;
      const consumer = { field, dal, custom_param: customParam, line: fieldEntry.line };
      if (pbField !== null) consumer.pb_field = pbField;
      consumers.push(consumer);
    }
    result.set(name, { file: logicalFile, line: entry.line, consumers });
  }
  return result;
}

function parseAutoSyncConsumers(file) {
  return parseDatasetConsumers(
    file,
    'Logic/Asset/LC_AssetAutoSyncDataDefine.lua',
    name => name.startsWith('AUTO_SYNC_CLIENT_'),
  );
}

function parseAssetDatasetConsumers(file) {
  return parseDatasetConsumers(file, 'Logic/Asset/LC_AssetDatasetDefine.lua', () => true);
}

function parseAutoSyncBridge(file) {
  const lines = lineArray(file);
  const definitionLine = property => {
    const pattern = new RegExp(`^\\s*L0_1\\.${property}\\s*=\\s*L\\d+_1\\s*$`);
    const index = lines.findIndex(line => pattern.test(line));
    return index < 0 ? null : index + 1;
  };
  return {
    file: 'Logic/Asset/LC_AssetManager.lua',
    pre_decode_line: definitionLine('PreDecode'),
    auto_sync_line: definitionLine('AutoSync'),
  };
}

function readVarint(buffer, offset) {
  let value = 0n;
  let shift = 0n;
  let cursor = offset;
  while (cursor < buffer.length) {
    const byte = buffer[cursor++];
    value |= BigInt(byte & 0x7f) << shift;
    if ((byte & 0x80) === 0) return { value, offset: cursor };
    shift += 7n;
    if (shift > 70n) throw new Error(`invalid varint at ${offset}`);
  }
  throw new Error(`truncated varint at ${offset}`);
}

function protobufFields(buffer) {
  const result = [];
  let offset = 0;
  while (offset < buffer.length) {
    const key = readVarint(buffer, offset);
    offset = key.offset;
    const field = Number(key.value >> 3n);
    const wire = Number(key.value & 7n);
    if (wire === 0) {
      const item = readVarint(buffer, offset);
      offset = item.offset;
      result.push({ field, wire, value: item.value });
    } else if (wire === 1) {
      result.push({ field, wire, bytes: buffer.subarray(offset, offset + 8) });
      offset += 8;
    } else if (wire === 2) {
      const length = readVarint(buffer, offset);
      offset = length.offset;
      const count = Number(length.value);
      result.push({ field, wire, bytes: buffer.subarray(offset, offset + count) });
      offset += count;
    } else if (wire === 5) {
      result.push({ field, wire, bytes: buffer.subarray(offset, offset + 4) });
      offset += 4;
    } else {
      throw new Error(`unsupported protobuf wire type ${wire}`);
    }
    if (offset > buffer.length) throw new Error('truncated protobuf field');
  }
  return result;
}

function stringField(fields, number) {
  const item = fields.find(field => field.field === number && field.wire === 2);
  return item ? item.bytes.toString('utf8') : '';
}

function integerField(fields, number, fallback = null) {
  const item = fields.find(field => field.field === number && field.wire === 0);
  return item ? Number(item.value) : fallback;
}

const FIELD_LABELS = {
  1: 'optional',
  2: 'required',
  3: 'repeated',
};

const FIELD_TYPES = {
  1: ['double', 'fixed64'],
  2: ['float', 'fixed32'],
  3: ['int64', 'varint'],
  4: ['uint64', 'varint'],
  5: ['int32', 'varint'],
  6: ['fixed64', 'fixed64'],
  7: ['fixed32', 'fixed32'],
  8: ['bool', 'varint'],
  9: ['string', 'length_delimited'],
  10: ['group', 'group'],
  11: ['message', 'length_delimited'],
  12: ['bytes', 'length_delimited'],
  13: ['uint32', 'varint'],
  14: ['enum', 'varint'],
  15: ['sfixed32', 'fixed32'],
  16: ['sfixed64', 'fixed64'],
  17: ['sint32', 'varint'],
  18: ['sint64', 'varint'],
};

function parseBooleanOption(buffer, number) {
  if (!buffer) return null;
  const value = integerField(protobufFields(buffer), number);
  return value === null ? null : value !== 0;
}

function parseField(buffer) {
  const fields = protobufFields(buffer);
  const labelNumber = integerField(fields, 4);
  const typeNumber = integerField(fields, 5);
  const [type, wireType] = FIELD_TYPES[typeNumber] || [`unknown_${typeNumber}`, 'unknown'];
  const options = fields.find(field => field.field === 8 && field.wire === 2)?.bytes;
  const repeatedPackable = labelNumber === 3 && ![9, 10, 11, 12].includes(typeNumber);
  return {
    name: stringField(fields, 1),
    number: integerField(fields, 3),
    label: FIELD_LABELS[labelNumber] || `unknown_${labelNumber}`,
    type,
    type_name: stringField(fields, 6).replace(/^\./, ''),
    wire_type: repeatedPackable ? `${wireType}_or_packed_length_delimited` : wireType,
    default_value: stringField(fields, 7) || null,
    oneof_index: integerField(fields, 9),
    json_name: stringField(fields, 10) || null,
    proto3_optional: integerField(fields, 17, 0) !== 0,
    packed_option: parseBooleanOption(options, 2),
  };
}

function parseOneof(buffer) {
  return { name: stringField(protobufFields(buffer), 1) };
}

function parseEnumValue(buffer) {
  const fields = protobufFields(buffer);
  const raw = fields.find(field => field.field === 2 && field.wire === 0)?.value ?? 0n;
  const low = Number(raw & 0xffffffffn);
  return { name: stringField(fields, 1), number: low >= 0x80000000 ? low - 0x100000000 : low };
}

function parseEnum(buffer) {
  const fields = protobufFields(buffer);
  return {
    name: stringField(fields, 1),
    values: fields.filter(field => field.field === 2 && field.wire === 2).map(field => parseEnumValue(field.bytes)),
  };
}

function parseMessage(buffer) {
  const fields = protobufFields(buffer);
  const options = fields.find(field => field.field === 7 && field.wire === 2)?.bytes;
  return {
    name: stringField(fields, 1),
    fields: fields.filter(field => field.field === 2 && field.wire === 2).map(field => parseField(field.bytes)),
    nested: fields.filter(field => field.field === 3 && field.wire === 2).map(field => parseMessage(field.bytes)),
    enums: fields.filter(field => field.field === 4 && field.wire === 2).map(field => parseEnum(field.bytes)),
    oneofs: fields.filter(field => field.field === 8 && field.wire === 2).map(field => parseOneof(field.bytes)),
    map_entry: parseBooleanOption(options, 7) === true,
  };
}

function parseFileDescriptor(buffer) {
  const fields = protobufFields(buffer);
  return {
    name: stringField(fields, 1),
    package: stringField(fields, 2),
    dependencies: fields.filter(field => field.field === 3 && field.wire === 2).map(field => field.bytes.toString('utf8')),
    messages: fields.filter(field => field.field === 4 && field.wire === 2).map(field => parseMessage(field.bytes)),
    enums: fields.filter(field => field.field === 5 && field.wire === 2).map(field => parseEnum(field.bytes)),
    syntax: stringField(fields, 12) || 'proto2',
  };
}

function parseDescriptorSet(file) {
  const setFields = protobufFields(fs.readFileSync(file));
  const files = setFields.filter(field => field.field === 1 && field.wire === 2).map(field => parseFileDescriptor(field.bytes));
  const messages = new Map();
  const messageSchemas = new Map();
  const enums = [];
  let topLevelMessageCount = 0;

  function visitMessage(message, prefix, file) {
    const fullName = prefix ? `${prefix}.${message.name}` : message.name;
    messages.set(fullName, file.name);
    messageSchemas.set(fullName, {
      full_name: fullName,
      file: file.name,
      syntax: file.syntax,
      fields: message.fields.map(field => ({
        ...field,
        effective_packed: field.label === 'repeated' && field.wire_type.endsWith('_or_packed_length_delimited')
          ? field.packed_option ?? file.syntax === 'proto3'
          : false,
      })),
      oneofs: message.oneofs,
      map_entry: message.map_entry,
    });
    for (const item of message.enums) enums.push({ ...item, full_name: `${fullName}.${item.name}`, file: file.name });
    for (const nested of message.nested) visitMessage(nested, fullName, file);
  }

  for (const item of files) {
    topLevelMessageCount += item.messages.length;
    for (const message of item.messages) visitMessage(message, item.package, item);
    for (const value of item.enums) {
      enums.push({ ...value, full_name: item.package ? `${item.package}.${value.name}` : value.name, file: item.name });
    }
  }
  return { files, messages, messageSchemas, enums, topLevelMessageCount };
}

function normalizeEnumName(name) {
  return name.replace(/^TT_/, '').replace(/^T_/, '');
}

function tsvValue(value) {
  if (value === null || value === undefined) return '';
  if (Array.isArray(value)) return value.map(tsvValue).join('|');
  if (typeof value === 'object') return JSON.stringify(value).replace(/[\t\r\n]/g, ' ');
  return String(value).replace(/[\t\r\n]/g, ' ');
}

function writeTsv(file, rows, columns) {
  const lines = [columns.join('\t')];
  for (const row of rows) lines.push(columns.map(column => tsvValue(row[column])).join('\t'));
  fs.writeFileSync(file, `${lines.join('\n')}\n`);
}

function countBy(rows, selector) {
  const result = {};
  for (const row of rows) {
    const key = selector(row);
    result[key] = (result[key] || 0) + 1;
  }
  return Object.fromEntries(Object.entries(result).sort(([a], [b]) => a.localeCompare(b)));
}

function luaFilesBelow(root) {
  const result = [];
  function visit(directory) {
    const entries = fs.readdirSync(directory, { withFileTypes: true })
      .sort((a, b) => a.name.localeCompare(b.name));
    for (const entry of entries) {
      const absolute = path.join(directory, entry.name);
      if (entry.isDirectory()) visit(absolute);
      else if (entry.isFile() && entry.name.toLowerCase().endsWith('.lua')) result.push(absolute);
    }
  }
  visit(root);
  return result;
}

function isDevelopmentLua(logicalFile) {
  const normalized = logicalFile.replace(/\\/g, '/');
  return /(^|\/)Editors?\//i.test(normalized)
    || /(^|\/)[^/]*(?:Test|Mock|Profiler)[^/]*\.lua$/i.test(normalized);
}

function firstLuaArgument(argumentsText) {
  let quote = null;
  let escaped = false;
  let depth = 0;
  for (let index = 0; index < argumentsText.length; index++) {
    const character = argumentsText[index];
    if (quote) {
      if (escaped) escaped = false;
      else if (character === '\\') escaped = true;
      else if (character === quote) quote = null;
      continue;
    }
    if (character === '"' || character === "'") {
      quote = character;
      continue;
    }
    if ('([{'.includes(character)) depth++;
    else if (')]}'.includes(character)) depth--;
    else if (character === ',' && depth === 0) return argumentsText.slice(0, index).trim();
  }
  return argumentsText.trim();
}

function parseGameSenders(luaRoot) {
  if (!fs.statSync(luaRoot).isDirectory()) throw new Error(`Lua root is not a directory: ${luaRoot}`);
  const luaFiles = luaFilesBelow(luaRoot);
  const rows = [];
  let candidateFiles = 0;
  let rawLoads = 0;
  let loads = 0;
  let confirmed = 0;

  for (const absolute of luaFiles) {
    const text = fs.readFileSync(absolute, 'utf8');
    if (!/\.SendMessage\b/.test(text)) continue;
    candidateFiles++;
    const logicalFile = path.relative(luaRoot, absolute).split(path.sep).join('/');
    const development = isDevelopmentLua(logicalFile);
    const lines = text.split(/\r?\n/);
    let registers = new Map();
    const blocks = [];

    function openFunction() {
      blocks.push({ type: 'function', parent: registers });
      registers = new Map();
    }

    function openBlock(type = 'block') {
      blocks.push({ type, entry: new Map(registers) });
    }

    function sameSymbol(left, right) {
      return left?.kind === right?.kind
        && left?.line === right?.line
        && left?.name === right?.name
        && left?.reason === right?.reason;
    }

    function closeBlock() {
      const block = blocks.pop();
      if (block?.type === 'function') {
        registers = block.parent;
      } else if (block) {
        const merged = new Map();
        for (const [name, symbol] of block.entry) {
          const current = registers.get(name);
          if (sameSymbol(symbol, current)) merged.set(name, current);
        }
        registers = merged;
      }
    }

    for (let index = 0; index < lines.length; index++) {
      const code = lines[index].replace(/--.*$/, '').trim();
      if (!code) continue;

      const structural = code
        .replace(/"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'/g, ' ')
        .match(/\b(function|elseif|else|if|for|while|repeat|do|end|until)\b/g) || [];
      let pendingLoopDo = 0;
      for (const token of structural) {
        if (token === 'function') openFunction();
        else if (token === 'if') openBlock('if');
        else if (token === 'elseif' || token === 'else') {
          const block = blocks.at(-1);
          if (block?.type === 'if') registers = new Map(block.entry);
        }
        else if (token === 'for' || token === 'while') {
          openBlock('loop');
          pendingLoopDo++;
        } else if (token === 'repeat') openBlock('repeat');
        else if (token === 'do') {
          if (pendingLoopDo > 0) pendingLoopDo--;
          else openBlock();
        } else if (token === 'end' || token === 'until') closeBlock();
      }
      if (structural.length > 0) continue;

      const call = code.match(/^\s*(?:(?:return)\s+|(?:local\s+)?[A-Za-z_][A-Za-z0-9_]*\s*=\s*)?([A-Za-z_][A-Za-z0-9_]*)\s*\((.*)\)\s*;?\s*$/);
      if (call) {
        const sender = registers.get(call[1]);
        if (sender?.kind === 'sender' || sender?.kind === 'unconfirmed_sender') {
          const argument = firstLuaArgument(call[2]);
          const argumentRegister = /^[A-Za-z_][A-Za-z0-9_]*$/.test(argument)
            ? registers.get(argument)
            : null;
          const callConfirmed = sender.kind === 'sender';
          const resolved = callConfirmed && argumentRegister?.kind === 'message_name';
          rows.push({
            file: logicalFile,
            line: resolved ? argumentRegister.line : sender.line,
            call_line: index + 1,
            name: resolved ? argumentRegister.name : null,
            development,
            confirmed: callConfirmed,
            resolved,
            unresolved_kind: resolved
              ? null
              : !callConfirmed
                ? sender.reason
                : /^-?\d+$/.test(argument) ? 'numeric' : 'parameter_or_dynamic',
          });
        }
      }

      const assignment = code.match(/^\s*(?:local\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*;?\s*$/);
      if (assignment) {
        const [, target, rhs] = assignment;
        let symbol = null;
        if (rhs === 'NW_Helper') {
          symbol = { kind: 'helper' };
        } else if (rhs === 'ProtobufNameMap') {
          symbol = { kind: 'name_map' };
        } else {
          const property = rhs.match(/^([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)$/);
          if (property) {
            const source = property[1] === 'NW_Helper' ? { kind: 'helper' } : registers.get(property[1]);
            if (property[2] === 'SendMessage') {
              rawLoads++;
              if (target === property[1]) {
                loads++;
                if (source?.kind === 'helper') {
                  confirmed++;
                  symbol = { kind: 'sender', line: index + 1 };
                }
              } else {
                symbol = { kind: 'unconfirmed_sender', line: index + 1, reason: 'cross_register_send_message' };
              }
            } else if (property[2] === 'ProtobufNameMap' && source?.kind === 'helper') {
              symbol = { kind: 'name_map' };
            } else if (source?.kind === 'name_map') {
              symbol = { kind: 'message_name', name: property[2], line: index + 1 };
            }
          }
        }
        if (symbol) registers.set(target, symbol);
        else registers.delete(target);
        continue;
      }

      const assignmentTargets = code.match(/^\s*(?:local\s+)?((?:[A-Za-z_][A-Za-z0-9_]*\s*,\s*)+[A-Za-z_][A-Za-z0-9_]*)\s*(?:=|$)/);
      if (assignmentTargets) {
        for (const target of assignmentTargets[1].split(',').map(value => value.trim())) registers.delete(target);
      }
    }
  }

  rows.sort((a, b) => a.file.localeCompare(b.file) || a.call_line - b.call_line);
  const resolved = rows.filter(row => row.confirmed && row.resolved).length;
  const unresolved = rows.filter(row => row.confirmed && !row.resolved).length;
  const unconfirmed = rows.filter(row => !row.confirmed).length;
  return {
    rows,
    summary: {
      lua_files_scanned: luaFiles.length,
      candidate_files: candidateFiles,
      raw_loads: rawLoads,
      loads,
      confirmed,
      resolved,
      unresolved,
      unconfirmed,
      unique_names: new Set(rows.map(row => row.name).filter(Boolean)).size,
    },
  };
}

function buildMatrix(options) {
  const schemaRows = [];
  for (const [relative, family] of SCHEMA_FILES) {
    const absolute = path.join(options.lua, ...relative.split('/'));
    if (!fs.existsSync(absolute)) throw new Error(`missing Lua registry: ${absolute}`);
    schemaRows.push(...parseSchemaFile(absolute, relative, family));
  }

  const schemasById = new Map();
  for (const schema of schemaRows) {
    if (!Number.isInteger(schema.id)) throw new Error(`schema without integer id: ${schema.name}`);
    if (!schemasById.has(schema.id)) schemasById.set(schema.id, []);
    schemasById.get(schema.id).push(schema);
  }

  const gameHandlerFile = path.join(options.lua, 'Network', 'NW_GameServerHandler.lua');
  const sceneSyncFile = path.join(options.lua, 'Logic', 'Scene', 'LC_Scene_Sync.lua');
  const baseHandlerFile = path.join(options.lua, 'Network', 'NW_ServerClientHandler.lua');
  const autoSyncConsumerFile = path.join(options.lua, 'Logic', 'Asset', 'LC_AssetAutoSyncDataDefine.lua');
  const assetManagerFile = path.join(options.lua, 'Logic', 'Asset', 'LC_AssetManager.lua');
  const assetDatasetFile = path.join(options.lua, 'Logic', 'Asset', 'LC_AssetDatasetDefine.lua');
  for (const file of [gameHandlerFile, sceneSyncFile, baseHandlerFile, autoSyncConsumerFile, assetManagerFile, assetDatasetFile]) {
    if (!fs.existsSync(file)) throw new Error(`missing Lua consumer: ${file}`);
  }
  const inputHashes = {
    descriptor_sha256: sha256(options.descriptor),
    lua_registry_sha256: Object.fromEntries(SCHEMA_FILES.map(([relative]) => [relative, sha256(path.join(options.lua, ...relative.split('/')))])),
    callback_lua_sha256: sha256(gameHandlerFile),
    scene_consumer_lua_sha256: sha256(sceneSyncFile),
    base_handler_lua_sha256: sha256(baseHandlerFile),
    autosync_consumer_lua_sha256: sha256(autoSyncConsumerFile),
    asset_manager_lua_sha256: sha256(assetManagerFile),
    asset_dataset_lua_sha256: sha256(assetDatasetFile),
  };
  const inputProfile = inputProfileFor(inputHashes);
  const callbacks = parseNameHandlerMappings(gameHandlerFile, 'Network/NW_GameServerHandler.lua', gameCallbackBound);
  const sceneConsumers = parseNameHandlerMappings(sceneSyncFile, 'Logic/Scene/LC_Scene_Sync.lua', sceneConsumerBound);
  const baseRoutes = parseBaseRoutes(baseHandlerFile);
  const autoSyncConsumers = parseAutoSyncConsumers(autoSyncConsumerFile);
  const autoSyncBridge = parseAutoSyncBridge(assetManagerFile);
  const assetDatasetConsumers = parseAssetDatasetConsumers(assetDatasetFile);
  const senderRegistry = parseGameSenders(options.lua);
  const sendersByName = new Map();
  for (const sender of senderRegistry.rows) {
    if (!sender.name) continue;
    if (!sendersByName.has(sender.name)) sendersByName.set(sender.name, []);
    sendersByName.get(sender.name).push(sender);
  }

  const descriptor = parseDescriptorSet(options.descriptor);
  const msgType = descriptor.enums.find(item => item.full_name === 'romsg.MSG_Type');
  if (!msgType) throw new Error('romsg.MSG_Type was not found in descriptor set');
  const descriptorById = new Map(msgType.values.map(item => [item.number, {
    enum_name: item.name,
    name: normalizeEnumName(item.name),
    file: msgType.file,
    enum: msgType.full_name,
  }]));

  const schemaNameToId = new Map();
  for (const [id, group] of schemasById) for (const row of group) schemaNameToId.set(row.name, id);
  const descriptorNameToId = new Map([...descriptorById].map(([id, item]) => [item.name, id]));
  const callbackByName = new Map(callbacks.map(item => [item.name, item]));
  const sceneByName = new Map(sceneConsumers.map(item => [item.name, item]));
  const routeByKind = {};
  for (const [kind, values] of Object.entries(baseRoutes)) routeByKind[kind] = new Map(values.map(item => [item.name, item]));

  const numericIds = new Set([...schemasById.keys(), ...descriptorById.keys()]);
  const rows = [];
  const representedCallbacks = new Set();
  const representedScene = new Set();
  for (const id of [...numericIds].sort((a, b) => a - b)) {
    const schemaGroup = schemasById.get(id) || [];
    const schema = schemaGroup.at(-1) || null;
    const descriptorItem = descriptorById.get(id) || null;
    const candidateNames = new Set([
      ...schemaGroup.map(item => item.name),
      ...(descriptorItem ? [descriptorItem.name] : []),
    ]);
    const callback = [...candidateNames].map(name => callbackByName.get(name)).find(Boolean) || null;
    const sceneConsumer = [...candidateNames].map(name => sceneByName.get(name)).find(Boolean) || null;
    const clientSenders = [...new Map(
      [...candidateNames]
        .flatMap(name => sendersByName.get(name) || [])
        .map(sender => [`${sender.file}:${sender.call_line}`, sender]),
    ).values()];
    const observedRole = observedRoleFor({ client_senders: clientSenders, callback, scene_consumer: sceneConsumer });
    const direction = directionFor({ ...(schema || {}), client_senders: clientSenders, callback, scene_consumer: sceneConsumer }, observedRole);
    if (callback) representedCallbacks.add(callback.name);
    if (sceneConsumer) representedScene.add(sceneConsumer.name);

    const requestPresent = schema?.request_type ? descriptor.messages.has(schema.request_type) : null;
    const responsePresent = schema?.response_type ? descriptor.messages.has(schema.response_type) : null;
    const autoSyncConsumer = schema?.source.family === 'autosync'
      ? autoSyncConsumers.get(schema.name) || { file: 'Logic/Asset/LC_AssetAutoSyncDataDefine.lua', line: null, consumers: [] }
      : null;
    const assetDatasetConsumer = [...candidateNames].map(name => assetDatasetConsumers.get(name)).find(Boolean) || null;
    const anomalies = [];
    if (schemaGroup.length > 1) anomalies.push('duplicate_schema_id');
    if (schema && descriptorItem && schema.name !== descriptorItem.name) anomalies.push('schema_descriptor_name_mismatch');
    if (schema?.request_type && !requestPresent) anomalies.push('request_type_missing_from_descriptor');
    if (schema?.response_type && !responsePresent) anomalies.push('response_type_missing_from_descriptor');
    if (callback && !schema) anomalies.push('registered_without_lua_schema');
    if (sceneConsumer && !callback) anomalies.push('scene_consumer_without_game_callback');
    if (autoSyncConsumer && autoSyncConsumer.consumers.length === 0) anomalies.push('autosync_dal_mapping_absent');
    if (assetDatasetConsumer && (requestPresent === false || responsePresent === false)) {
      anomalies.push('asset_dataset_consumer_with_incomplete_schema');
    }

    let reachability;
    if (callback && sceneConsumer) reachability = 'explicit_game_callback_and_scene_consumer';
    else if (callback) reachability = 'explicit_game_callback';
    else if (sceneConsumer) reachability = 'scene_consumer_without_game_callback';
    else if (clientSenders.length > 0) reachability = 'explicit_client_sender';
    else if (schema?.source.family === 'autosync') reachability = 'generic_autosync_schema';
    else if (schema) reachability = 'schema_without_explicit_callback';
    else reachability = 'descriptor_enum_only';

    const routeName = schema?.name || descriptorItem?.name || '';
    rows.push({
      opcode: id,
      name: routeName,
      direction,
      schema_shape: schema ? schemaShape(schema) : 'unknown',
      observed_role: observedRole,
      client_senders: clientSenders,
      request_type: schema?.request_type || null,
      response_type: schema?.response_type || null,
      lua_message_type: schema?.message_type ?? null,
      schema_sources: schemaGroup.map(item => item.source),
      schema_family: schema?.source.family || null,
      descriptor_enum_present: Boolean(descriptorItem),
      descriptor_enum_name: descriptorItem?.enum_name || null,
      descriptor_source_file: descriptorItem?.file || null,
      request_type_in_descriptor: requestPresent,
      response_type_in_descriptor: responsePresent,
      request_type_source_file: requestPresent ? descriptor.messages.get(schema.request_type) : null,
      response_type_source_file: responsePresent ? descriptor.messages.get(schema.response_type) : null,
      callback: callback ? { handler: callback.handler, file: callback.file, line: callback.line } : null,
      scene_consumer: sceneConsumer ? { handler: sceneConsumer.handler, file: sceneConsumer.file, line: sceneConsumer.line } : null,
      autosync_consumer: autoSyncConsumer ? {
        handler: 'LC_AssetManager.PreDecode -> LC_AssetManager.AutoSync',
        file: autoSyncBridge.file,
        line: autoSyncBridge.pre_decode_line,
        auto_sync_line: autoSyncBridge.auto_sync_line,
        mapping_file: autoSyncConsumer.file,
        mapping_line: autoSyncConsumer.line,
        dals: autoSyncConsumer.consumers,
      } : null,
      asset_dataset_consumer: assetDatasetConsumer ? {
        file: assetDatasetConsumer.file,
        line: assetDatasetConsumer.line,
        dals: assetDatasetConsumer.consumers,
      } : null,
      base_route: {
        queued_before_dispatch: routeByKind.queued_before_dispatch.get(routeName)?.value ?? null,
        decode_turn: routeByKind.decode_turn.get(routeName)?.value ?? null,
        no_decode: routeByKind.no_decode.get(routeName)?.value ?? null,
        bounded_counter: routeByKind.bounded_counter.get(routeName)?.value ?? null,
      },
      managed_bridge: inputProfile?.managed_bridge || null,
      reachability,
      anomalies,
    });
  }

  function addNameOnly(item, kind) {
    const resolvedId = schemaNameToId.get(item.name) ?? descriptorNameToId.get(item.name) ?? null;
    if (resolvedId !== null) return;
    const clientSenders = sendersByName.get(item.name) || [];
    const callback = kind === 'callback' ? { handler: item.handler, file: item.file, line: item.line } : null;
    const sceneConsumer = kind === 'scene' ? { handler: item.handler, file: item.file, line: item.line } : null;
    const observedRole = observedRoleFor({ client_senders: clientSenders, callback, scene_consumer: sceneConsumer });
    rows.push({
      opcode: null,
      name: item.name,
      direction: directionFor({ client_senders: clientSenders, callback, scene_consumer: sceneConsumer }, observedRole),
      schema_shape: 'unknown',
      observed_role: observedRole,
      client_senders: clientSenders,
      request_type: null,
      response_type: null,
      lua_message_type: null,
      schema_sources: [],
      schema_family: null,
      descriptor_enum_present: false,
      descriptor_enum_name: null,
      descriptor_source_file: null,
      request_type_in_descriptor: null,
      response_type_in_descriptor: null,
      request_type_source_file: null,
      response_type_source_file: null,
      callback,
      scene_consumer: sceneConsumer,
      autosync_consumer: null,
      asset_dataset_consumer: null,
      base_route: { queued_before_dispatch: null, decode_turn: null, no_decode: null, bounded_counter: null },
      managed_bridge: inputProfile?.managed_bridge || null,
      reachability: kind === 'callback' ? 'registered_without_schema' : 'scene_consumer_without_schema',
      anomalies: [kind === 'callback' ? 'registered_without_lua_schema' : 'scene_consumer_without_lua_schema'],
    });
  }
  for (const item of callbacks) if (!representedCallbacks.has(item.name)) addNameOnly(item, 'callback');
  for (const item of sceneConsumers) if (!representedScene.has(item.name)) addNameOnly(item, 'scene');

  rows.sort((a, b) => {
    if (a.opcode === null && b.opcode !== null) return 1;
    if (a.opcode !== null && b.opcode === null) return -1;
    return (a.opcode ?? 0) - (b.opcode ?? 0) || a.name.localeCompare(b.name);
  });

  const families = countBy(schemaRows, row => row.source.family);
  const descriptorNonzero = msgType.values.filter(item => item.number !== 0);
  const descriptorOnly = rows.filter(row => row.reachability === 'descriptor_enum_only');
  for (const row of descriptorOnly) {
    if (row.opcode === 0 || /_(?:START|END)$/.test(row.name)) {
      row.registry_class = 'sentinel';
      row.descriptor_message_candidates = [];
    } else {
      const override = inputProfile?.descriptor_only[row.opcode] || null;
      row.registry_class = override?.registry_class || 'stale_or_external_unknown';
      row.descriptor_message_candidates = override?.descriptor_message_candidates || [];
      if (override?.paired_schema_opcode !== undefined) row.paired_schema_opcode = override.paired_schema_opcode;
    }
  }
  const callbackOnly = rows.filter(row => row.reachability === 'registered_without_schema');
  const sceneOnly = rows.filter(row => row.reachability === 'scene_consumer_without_schema');
  const nullNull = rows.filter(row => row.schema_shape === 'empty_both');
  const unresolvedTypes = rows.filter(row => row.anomalies.some(item => item.endsWith('_type_missing_from_descriptor')));
  for (const row of unresolvedTypes) {
    const missing = [
      row.request_type_in_descriptor === false ? row.request_type : null,
      row.response_type_in_descriptor === false ? row.response_type : null,
    ].filter(Boolean);
    if (missing.some(item => item === 'romsg.' || item === 'ezmsg.')) row.schema_incomplete_class = 'malformed_empty_type_name';
    else if (missing.some(item => item === 'romsg.null' || item === 'ezmsg.NullArg')) row.schema_incomplete_class = 'noncanonical_null_alias';
    else if (inputProfile && [31110, 31111].includes(row.opcode)) row.schema_incomplete_class = 'registered_stale_special_state_schema';
    else if (inputProfile && row.opcode === 31114) row.schema_incomplete_class = 'unregistered_stale_special_state_schema';
    else row.schema_incomplete_class = 'stale_or_descriptor_skew_unknown';
  }
  const luaOnlyEz = rows.filter(row => row.schema_family === 'ezprotobuf' && !row.descriptor_enum_present);
  const autoSyncOverlay = rows.filter(row => row.schema_family === 'autosync' && !row.descriptor_enum_present);
  const rowsWithAssetDatasetConsumer = rows.filter(row => row.asset_dataset_consumer);
  const incompleteWithAssetDatasetConsumer = unresolvedTypes.filter(row => row.asset_dataset_consumer);
  const incompleteWithConcreteDownstreamConsumer = unresolvedTypes.filter(row => row.scene_consumer || row.asset_dataset_consumer);
  const sceneWithoutGameCallback = rows.filter(row => row.reachability === 'scene_consumer_without_game_callback');
  const opcodeRootTypes = new Set();
  for (const row of rows) {
    if (row.request_type_in_descriptor) opcodeRootTypes.add(row.request_type);
    if (row.response_type_in_descriptor) opcodeRootTypes.add(row.response_type);
    for (const candidate of row.descriptor_message_candidates || []) {
      if (descriptor.messageSchemas.has(candidate)) opcodeRootTypes.add(candidate);
    }
  }
  const enumSchemas = new Map(descriptor.enums.map(item => [item.full_name, item]));
  const closureMessages = new Set();
  const closureEnums = new Set();
  const unresolvedClosureTypes = new Set();
  const pendingTypes = [...opcodeRootTypes];
  while (pendingTypes.length > 0) {
    const type = pendingTypes.pop();
    if (descriptor.messageSchemas.has(type)) {
      if (closureMessages.has(type)) continue;
      closureMessages.add(type);
      for (const field of descriptor.messageSchemas.get(type).fields) {
        if (field.type_name) pendingTypes.push(field.type_name);
      }
    } else if (enumSchemas.has(type)) {
      closureEnums.add(type);
    } else {
      unresolvedClosureTypes.add(type);
    }
  }
  const wireSchemaCatalog = {
    static_only: true,
    descriptor_sha256: sha256(options.descriptor),
    opcode_root_types: [...opcodeRootTypes].sort(),
    closure_message_types: [...closureMessages].sort().map(name => descriptor.messageSchemas.get(name)),
    closure_enum_types: [...closureEnums].sort().map(name => enumSchemas.get(name)),
    unresolved_type_references: [...unresolvedClosureTypes].sort(),
  };
  const runtimeSchemaIds = new Set(schemaRows.map(row => row.id));
  const legacy1045 = msgType.values.length + (families.ezprotobuf || 0);
  const summary = {
    static_only: true,
    runtime_capture_complete: false,
    input_profile: inputProfile?.id || null,
    input_hashes: inputHashes,
    managed_bridge_evidence: inputProfile?.managed_bridge_evidence || null,
    client_sender_scan: senderRegistry.summary,
    counts: {
      descriptor_files: descriptor.files.length,
      descriptor_message_types_top_level: descriptor.topLevelMessageCount,
      descriptor_message_types_recursive: descriptor.messages.size,
      descriptor_msg_type_values_including_none: msgType.values.length,
      descriptor_msg_type_values_nonzero: descriptorNonzero.length,
      opcode_wire_root_types: opcodeRootTypes.size,
      opcode_wire_closure_messages: closureMessages.size,
      opcode_wire_closure_enums: closureEnums.size,
      opcode_wire_unresolved_references: unresolvedClosureTypes.size,
      lua_schema_rows: schemaRows.length,
      lua_schema_unique_ids: runtimeSchemaIds.size,
      lua_schema_families: families,
      game_callbacks: callbacks.length,
      scene_consumers: sceneConsumers.length,
      client_sender_raw_loads: senderRegistry.summary.raw_loads,
      client_sender_loads: senderRegistry.summary.loads,
      client_sender_confirmed: senderRegistry.summary.confirmed,
      client_sender_resolved: senderRegistry.summary.resolved,
      client_sender_unresolved: senderRegistry.summary.unresolved,
      client_sender_unconfirmed: senderRegistry.summary.unconfirmed,
      client_sender_unique_names: senderRegistry.summary.unique_names,
      matrix_rows: rows.length,
      directions: countBy(rows.filter(row => row.opcode !== null), row => row.direction),
      observed_roles: countBy(rows, row => row.observed_role),
      reachability: countBy(rows, row => row.reachability),
      descriptor_only: descriptorOnly.length,
      descriptor_only_classes: countBy(descriptorOnly, row => row.registry_class),
      lua_only_ez: luaOnlyEz.length,
      autosync_overlay_without_msg_type_enum: autoSyncOverlay.length,
      autosync_with_dal_mapping: autoSyncOverlay.filter(row => row.autosync_consumer?.dals.length > 0).length,
      autosync_without_dal_mapping: autoSyncOverlay.filter(row => row.autosync_consumer?.dals.length === 0).length,
      asset_dataset_definitions: assetDatasetConsumers.size,
      matrix_rows_with_asset_dataset_consumer: rowsWithAssetDatasetConsumer.length,
      schema_incomplete_with_asset_dataset_consumer: incompleteWithAssetDatasetConsumer.length,
      schema_incomplete_with_concrete_downstream_consumer: incompleteWithConcreteDownstreamConsumer.length,
      registered_without_schema: callbackOnly.length,
      scene_consumer_without_schema: sceneOnly.length,
      scene_consumer_without_game_callback: sceneWithoutGameCallback.length,
      null_null: nullNull.length,
      unresolved_schema_types: unresolvedTypes.length,
      schema_incomplete_classes: countBy(unresolvedTypes, row => row.schema_incomplete_class),
    },
    count_reconciliation: {
      prior_1012: {
        value: families.protobuf || 0,
        meaning: 'NW_Helper_PBDefine.lua rows',
      },
      prior_1045: {
        value: legacy1045,
        formula: 'romsg.MSG_Type values including TT_None + EZ protobuf rows',
        disjoint_id_sets: [...schemasById.entries()]
          .filter(([, group]) => group.at(-1).source.family === 'ezprotobuf')
          .every(([id]) => !descriptorById.has(id)),
      },
      prior_113: {
        value: callbacks.length,
        meaning: 'explicit NW_GameServerHandler.KVMsgFunc callback registrations, not total protocol rows',
      },
      complete_lua_runtime_schema_map: {
        value: runtimeSchemaIds.size,
        formula: 'PBDefine + EZPBDefine + AutoSyncClient_Define unique ids',
      },
    },
    unknowns: [
      'Static registration cannot prove an opcode was sent, received, or rendered at runtime.',
      'A schema without an explicit NW_GameServerHandler callback may be consumed by request/response dispatch, generic AutoSync processing, a dynamically registered direct handler, or another path.',
      'Descriptor-only enum values are reserved protocol definitions; absence from the Lua ProtobufMap does not prove the server never uses them in another build.',
      '31109 and 31119 producer trigger rules remain UNKNOWN without an official same-build trace.',
    ],
  };

  return {
    rows, summary, descriptorOnly, luaOnlyEz, autoSyncOverlay, callbackOnly, sceneOnly,
    sceneWithoutGameCallback, nullNull, unresolvedTypes, callbacks, sceneConsumers, baseRoutes,
    clientSenderRegistry: senderRegistry.rows,
    assetDatasetRegistry: [...assetDatasetConsumers].map(([name, value]) => ({ name, ...value })),
    wireSchemaCatalog,
  };
}

function emit(result, outDir) {
  fs.mkdirSync(outDir, { recursive: true });
  fs.writeFileSync(path.join(outDir, 'opcode-matrix.json'), `${JSON.stringify(result.rows, null, 2)}\n`);
  writeTsv(path.join(outDir, 'opcode-matrix.tsv'), result.rows.map(row => ({
    opcode: row.opcode,
    name: row.name,
    direction: row.direction,
    schema_shape: row.schema_shape,
    observed_role: row.observed_role,
    client_senders: row.client_senders,
    request_type: row.request_type,
    response_type: row.response_type,
    schema_family: row.schema_family,
    schema_sources: row.schema_sources,
    descriptor_enum_name: row.descriptor_enum_name,
    request_type_in_descriptor: row.request_type_in_descriptor,
    response_type_in_descriptor: row.response_type_in_descriptor,
    callback: row.callback?.handler,
    callback_source: row.callback ? `${row.callback.file}:${row.callback.line}` : null,
    scene_consumer: row.scene_consumer?.handler,
    scene_consumer_source: row.scene_consumer ? `${row.scene_consumer.file}:${row.scene_consumer.line}` : null,
    autosync_consumer: row.autosync_consumer?.handler,
    autosync_dals: row.autosync_consumer?.dals,
    asset_dataset_dals: row.asset_dataset_consumer?.dals,
    base_route: row.base_route,
    reachability: row.reachability,
    anomalies: row.anomalies,
  })), [
    'opcode', 'name', 'direction', 'schema_shape', 'observed_role', 'client_senders',
    'request_type', 'response_type', 'schema_family', 'schema_sources',
    'descriptor_enum_name', 'request_type_in_descriptor', 'response_type_in_descriptor', 'callback',
    'callback_source', 'scene_consumer', 'scene_consumer_source', 'autosync_consumer', 'autosync_dals', 'asset_dataset_dals',
    'base_route', 'reachability', 'anomalies',
  ]);
  fs.writeFileSync(path.join(outDir, 'summary.json'), `${JSON.stringify(result.summary, null, 2)}\n`);
  fs.writeFileSync(path.join(outDir, 'opcode-wire-schemas.json'), `${JSON.stringify(result.wireSchemaCatalog, null, 2)}\n`);
  fs.writeFileSync(path.join(outDir, 'descriptor-only.json'), `${JSON.stringify(result.descriptorOnly, null, 2)}\n`);
  writeTsv(path.join(outDir, 'descriptor-only.tsv'), result.descriptorOnly, [
    'opcode', 'name', 'descriptor_enum_name', 'registry_class', 'descriptor_message_candidates', 'paired_schema_opcode',
  ]);
  fs.writeFileSync(path.join(outDir, 'lua-only-ez.json'), `${JSON.stringify(result.luaOnlyEz, null, 2)}\n`);
  writeTsv(path.join(outDir, 'lua-only-ez.tsv'), result.luaOnlyEz, [
    'opcode', 'name', 'direction', 'request_type', 'response_type', 'request_type_in_descriptor', 'response_type_in_descriptor',
  ]);
  fs.writeFileSync(path.join(outDir, 'autosync-overlay.json'), `${JSON.stringify(result.autoSyncOverlay, null, 2)}\n`);
  writeTsv(path.join(outDir, 'autosync-overlay.tsv'), result.autoSyncOverlay, [
    'opcode', 'name', 'direction', 'response_type', 'lua_message_type', 'autosync_consumer', 'reachability',
  ]);
  fs.writeFileSync(path.join(outDir, 'registered-without-schema.json'), `${JSON.stringify(result.callbackOnly, null, 2)}\n`);
  fs.writeFileSync(path.join(outDir, 'scene-consumer-without-schema.json'), `${JSON.stringify(result.sceneOnly, null, 2)}\n`);
  fs.writeFileSync(path.join(outDir, 'scene-consumer-without-game-callback.json'), `${JSON.stringify(result.sceneWithoutGameCallback, null, 2)}\n`);
  fs.writeFileSync(path.join(outDir, 'null-null.json'), `${JSON.stringify(result.nullNull, null, 2)}\n`);
  fs.writeFileSync(path.join(outDir, 'unresolved-types.json'), `${JSON.stringify(result.unresolvedTypes, null, 2)}\n`);
  fs.writeFileSync(path.join(outDir, 'schema-incomplete.json'), `${JSON.stringify(result.unresolvedTypes, null, 2)}\n`);
  writeTsv(path.join(outDir, 'schema-incomplete.tsv'), result.unresolvedTypes, [
    'opcode', 'name', 'direction', 'request_type', 'response_type', 'request_type_in_descriptor',
    'response_type_in_descriptor', 'callback', 'scene_consumer', 'schema_incomplete_class', 'anomalies',
  ]);
  fs.writeFileSync(path.join(outDir, 'callback-registry.json'), `${JSON.stringify(result.callbacks, null, 2)}\n`);
  fs.writeFileSync(path.join(outDir, 'scene-consumer-registry.json'), `${JSON.stringify(result.sceneConsumers, null, 2)}\n`);
  fs.writeFileSync(path.join(outDir, 'client-sender-registry.json'), `${JSON.stringify(result.clientSenderRegistry, null, 2)}\n`);
  fs.writeFileSync(path.join(outDir, 'base-route-registry.json'), `${JSON.stringify(result.baseRoutes, null, 2)}\n`);
  fs.writeFileSync(path.join(outDir, 'asset-dataset-consumer-registry.json'), `${JSON.stringify(result.assetDatasetRegistry, null, 2)}\n`);
}

if (require.main === module) {
  try {
    const options = parseArgs(process.argv.slice(2));
    const result = buildMatrix(options);
    emit(result, options.out);
    console.log(`${result.rows.length} matrix rows -> ${options.out}`);
    console.log(JSON.stringify(result.summary.counts, null, 2));
  } catch (error) {
    console.error(error.stack || error.message || String(error));
    process.exitCode = 1;
  }
}

module.exports = {
  buildMatrix,
  directionFor,
  observedRoleFor,
  parseGameSenders,
  schemaShape,
  inputProfileFor,
  KNOWN_INPUT_PROFILES,
  parseDescriptorSet,
  parseNameHandlerMappings,
  parseAutoSyncBridge,
  parseAutoSyncConsumers,
  parseAssetDatasetConsumers,
  parseSchemaFile,
  protobufFields,
};
