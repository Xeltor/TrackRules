#!/usr/bin/env node
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

function ensureFourPartVersion(tag) {
  if (!tag) {
    throw new Error('Release tag is missing.');
  }
  const sanitized = tag.trim().replace(/^v/i, '');
  if (!sanitized) {
    throw new Error(`Release tag "${tag}" does not contain version digits.`);
  }
  const parts = sanitized.split('.').map((part) => part.trim()).filter((part) => part.length > 0);
  while (parts.length < 4) {
    parts.push('0');
  }
  if (parts.some((part) => !/^\d+$/.test(part))) {
    throw new Error(`Release tag "${tag}" must contain only numeric components for manifest compatibility.`);
  }
  return parts.slice(0, 4).map((part, index) => {
    if (index === 3) {
      // Ensure the revision always fits in 32-bit signed integer range just in case.
      const revision = Number(part);
      if (!Number.isFinite(revision) || revision < 0 || revision > 2147483647) {
        throw new Error(`Invalid revision component "${part}" in release tag "${tag}".`);
      }
      return String(revision);
    }
    return String(Number(part));
  });
}

function buildVersionStrings(tag) {
  const parts = ensureFourPartVersion(tag);
  const releaseVersion = parts.join('.');
  const debugParts = parts.slice();
  debugParts[3] = String(Number(debugParts[3]) + 1);
  const debugVersion = debugParts.join('.');
  return { releaseVersion, debugVersion };
}

function normalizeChangelog(base, qualifier) {
  const sections = [];
  const trimmed = (base || '').trim();
  if (trimmed) {
    sections.push(trimmed);
  }
  if (qualifier) {
    sections.push(qualifier);
  }
  return sections.join('\n\n');
}

function createOrUpdatePlugin(manifest, pluginTemplate) {
  const index = manifest.findIndex((entry) => entry.guid === pluginTemplate.guid);
  if (index === -1) {
    manifest.push({ ...pluginTemplate, versions: [] });
    return manifest[manifest.length - 1];
  }
  const plugin = manifest[index];
  for (const key of Object.keys(pluginTemplate)) {
    if (key !== 'versions') {
      plugin[key] = pluginTemplate[key];
    }
  }
  plugin.versions = Array.isArray(plugin.versions) ? plugin.versions : [];
  return plugin;
}

function main() {
  const manifestPath = process.argv[2] || 'manifest.json';
  const assetsDirectory = process.argv[3] || 'dist';
  const releasePayloadRaw = process.env.RELEASE_PAYLOAD;
  const targetAbi = process.env.TARGET_ABI || '10.9.0.0';

  if (!releasePayloadRaw) {
    throw new Error('RELEASE_PAYLOAD environment variable is required.');
  }

  const releasePayload = JSON.parse(releasePayloadRaw);
  const { releaseVersion, debugVersion } = buildVersionStrings(releasePayload.tag_name);
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  const pluginTemplate = {
    guid: 'f4903c07-0d28-4183-9960-f870d61d07a3',
    name: 'Track Rules',
    description: 'Server-side audio and subtitle default selection with Series → Library → Global rule precedence.',
    overview: 'Pick the right audio/subtitle tracks automatically per user, honoring Series → Library → Global precedence.',
    owner: 'Xelflix Labs',
    category: 'Playback'
  };
  const plugin = createOrUpdatePlugin(manifest, pluginTemplate);

  const assets = Array.isArray(releasePayload.assets) ? releasePayload.assets : [];
  if (!fs.existsSync(assetsDirectory)) {
    throw new Error(`Assets directory "${assetsDirectory}" does not exist.`);
  }

  const newEntries = [];
  for (const asset of assets) {
    if (!asset || typeof asset.name !== 'string') {
      continue;
    }
    if (!asset.name.toLowerCase().endsWith('.zip')) {
      continue; // Only ship zip bundles via manifest.
    }
    const assetPath = path.join(assetsDirectory, asset.name);
    if (!fs.existsSync(assetPath)) {
      console.warn(`Skipping asset ${asset.name} because it was not downloaded to ${assetsDirectory}.`);
      continue;
    }
    const fileBuffer = fs.readFileSync(assetPath);
    const checksum = crypto.createHash('sha256').update(fileBuffer).digest('hex');
    const isDebug = /debug/i.test(asset.name);
    const qualifier = isDebug ? 'Debug build' : 'Release build';
    const version = isDebug ? debugVersion : releaseVersion;
    const changelog = normalizeChangelog(releasePayload.body, qualifier);
    const timestamp = releasePayload.published_at || new Date().toISOString();

    newEntries.push({
      version,
      changelog,
      targetAbi,
      sourceUrl: asset.browser_download_url,
      checksum,
      timestamp
    });
  }

  if (newEntries.length === 0) {
    console.warn('No distributable assets were discovered; manifest will remain unchanged for this release.');
    return;
  }

  newEntries.sort((a, b) => {
    const score = (entry) => (/Debug build/i.test(entry.changelog) ? 1 : 0);
    return score(a) - score(b);
  });

  const existing = plugin.versions.filter((entry) => !newEntries.some((candidate) => candidate.version === entry.version));
  plugin.versions = [...newEntries, ...existing];

  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, 'utf8');
}

try {
  main();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exit(1);
}
