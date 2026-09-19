import fs from 'node:fs';
import path from 'node:path';

const apiBase = 'https://api.nexusmods.com/v3';
const apiKey = process.env.NEXUS_API_KEY;
const gameDomain = process.env.NEXUS_GAME_DOMAIN;
const modPageId = process.env.NEXUS_MOD_ID;
const filename = process.env.RELEASE_ASSET;
const version = process.env.MOD_VERSION;
const name = process.env.MOD_DISPLAY_NAME;
const description = process.env.MOD_DESCRIPTION || '';
const category = process.env.MOD_FILE_CATEGORY || 'main';

for (const [key, value] of Object.entries({ apiKey, gameDomain, modPageId, filename, version, name })) {
  if (!value) throw new Error(`Missing required value: ${key}`);
}
if (!fs.existsSync(filename)) throw new Error(`Release asset not found: ${filename}`);

const api = (url, options = {}) => fetch(`${apiBase}${url}`, {
  ...options,
  headers: {
    apikey: apiKey,
    'Content-Type': 'application/json',
    'User-Agent': 'AcTePuKc/TravellersRest-Translation-Mod Nexus bootstrap',
    ...(options.headers || {}),
  },
});

const expectJson = async (response, action) => {
  const text = await response.text();
  let body;
  try { body = text ? JSON.parse(text) : {}; } catch { body = { raw: text }; }
  if (!response.ok) throw new Error(`${action} failed (${response.status}): ${JSON.stringify(body)}`);
  return body;
};

const page = await expectJson(await api(`/games/${encodeURIComponent(gameDomain)}/mods/${encodeURIComponent(modPageId)}`), 'Validate Nexus mod page');
const modId = page?.data?.id;
if (!modId) throw new Error('Nexus mod page returned no API mod ID. No upload was attempted.');

const files = await expectJson(await api(`/mods/${encodeURIComponent(modId)}/files`), 'Check existing Nexus files');
const existingFiles = files?.data?.mod_files || [];
if (existingFiles.some((file) => file.name === name)) {
  console.log(`Nexus already has a file named '${name}'; skipping bootstrap for this release.`);
  process.exit(0);
}

const stat = fs.statSync(filename);
const createdUpload = await expectJson(await api('/uploads/multipart', {
  method: 'POST',
  body: JSON.stringify({ filename: path.basename(filename), size_bytes: String(stat.size) }),
}), 'Create Nexus upload session');
const upload = createdUpload.data;

if (!upload?.id || !Array.isArray(upload.part_presigned_urls) || !upload.complete_presigned_url) {
  throw new Error(`Unexpected upload-session response: ${JSON.stringify(createdUpload)}`);
}

const fd = fs.openSync(filename, 'r');
const parts = [];
try {
  for (let i = 0; i < upload.part_presigned_urls.length; i++) {
    const offset = i * upload.part_size_bytes;
    const length = Math.min(upload.part_size_bytes, Math.max(0, stat.size - offset));
    const buffer = Buffer.alloc(length);
    fs.readSync(fd, buffer, 0, length, offset);
    const response = await fetch(upload.part_presigned_urls[i], {
      method: 'PUT',
      headers: { 'Content-Type': 'application/octet-stream', 'Content-Length': String(length) },
      body: buffer,
    });
    if (!response.ok) throw new Error(`Upload part ${i + 1} failed (${response.status}): ${await response.text()}`);
    const etag = response.headers.get('etag');
    if (!etag) throw new Error(`Upload part ${i + 1} returned no ETag.`);
    parts.push({ partNumber: i + 1, etag: etag.replaceAll('"', '') });
  }
} finally {
  fs.closeSync(fd);
}

const xml = `<CompleteMultipartUpload>\n${parts.map((part) => `  <Part>\n    <PartNumber>${part.partNumber}</PartNumber>\n    <ETag>${part.etag}</ETag>\n  </Part>`).join('\n')}\n</CompleteMultipartUpload>`;
const completed = await fetch(upload.complete_presigned_url, { method: 'POST', headers: { 'Content-Type': 'application/xml' }, body: xml });
if (!completed.ok) throw new Error(`Complete multipart upload failed (${completed.status}): ${await completed.text()}`);

await expectJson(await api(`/uploads/${upload.id}/finalise`, { method: 'POST' }), 'Finalise Nexus upload');

let available = false;
for (let attempt = 0; attempt < 60; attempt++) {
  const state = await expectJson(await api(`/uploads/${upload.id}`), 'Check Nexus upload state');
  if (state?.data?.state === 'available') { available = true; break; }
  await new Promise((resolve) => setTimeout(resolve, 2000));
}
if (!available) throw new Error('Nexus upload did not become available in time. Inspect the Nexus page before retrying.');

const file = await expectJson(await api('/mod-files', {
  method: 'POST',
  body: JSON.stringify({
    upload_id: upload.id,
    mod_id: modId,
    name,
    description,
    version,
    file_category: category,
    primary_mod_manager_download: false,
    allow_mod_manager_download: true,
    show_requirements_pop_up: false,
    update_mod_version: true,
  }),
}), 'Create first Nexus mod file');

const fileId = file?.data?.game_scoped_id;
if (!fileId) throw new Error('Nexus created a file but returned no game-scoped file ID. Inspect the Nexus page before retrying.');
console.log(`Created Nexus file ID: ${fileId}`);
if (process.env.GITHUB_STEP_SUMMARY) {
  fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, `## Nexus bootstrap upload\n\nCreated the first Nexus file. Add this value as the \`NEXUS_FILE_ID_EMPLOYEEREFRESH\` GitHub secret before the next release:\n\n- **Nexus File ID:** \`${fileId}\`\n`);
}
