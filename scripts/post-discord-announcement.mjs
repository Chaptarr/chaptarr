#!/usr/bin/env node

import fs from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';

import { splitDiscordReleaseBody } from './discord-release-notes.mjs';

function required(name) {
  const value = process.env[name]?.trim();
  if (!value) {
    throw new Error(name + ' is required.');
  }
  return value;
}

function validateHttpsUrl(value, label) {
  let url;
  try {
    url = new URL(value);
  } catch {
    throw new Error(label + ' must be a valid HTTPS URL.');
  }
  if (url.protocol !== 'https:') {
    throw new Error(label + ' must be a valid HTTPS URL.');
  }
  return url.toString();
}

const webhookValue = required('DISCORD_RELEASE_WEBHOOK');
let webhookUrl;
try {
  webhookUrl = new URL(webhookValue);
} catch {
  throw new Error('DISCORD_RELEASE_WEBHOOK must be a Discord HTTPS webhook URL.');
}
if (
  webhookUrl.protocol !== 'https:' ||
  !['discord.com', 'discordapp.com'].includes(webhookUrl.hostname) ||
  !webhookUrl.pathname.startsWith('/api/webhooks/')
) {
  throw new Error('DISCORD_RELEASE_WEBHOOK must be a Discord HTTPS webhook URL.');
}
webhookUrl.searchParams.set('wait', 'true');

const title = required('ANNOUNCEMENT_TITLE');
const destination = validateHttpsUrl(required('ANNOUNCEMENT_URL'), 'ANNOUNCEMENT_URL');
const bodyValue = process.env.ANNOUNCEMENT_BODY;
const bodyFile = process.env.ANNOUNCEMENT_BODY_FILE;
const body =
  typeof bodyValue === 'string'
    ? bodyValue.trim()
    : bodyFile
      ? fs.readFileSync(bodyFile, 'utf8').trim()
      : '';

if (!body) {
  throw new Error('ANNOUNCEMENT_BODY or ANNOUNCEMENT_BODY_FILE is required.');
}

const chunks = splitDiscordReleaseBody(body);
if (chunks.length === 0) {
  throw new Error('The Discord announcement body is empty.');
}

const fields = [];
if (process.env.ANNOUNCEMENT_IMAGE_REF) {
  fields.push({
    name: 'Image',
    value: process.env.ANNOUNCEMENT_IMAGE_REF.slice(0, 1024),
    inline: false,
  });
}
if (process.env.ANNOUNCEMENT_IMAGE_DIGEST) {
  fields.push({
    name: 'Digest',
    value: process.env.ANNOUNCEMENT_IMAGE_DIGEST.slice(0, 1024),
    inline: false,
  });
}
if (process.env.ANNOUNCEMENT_WORKFLOW_URL) {
  fields.push({
    name: 'Workflow',
    value: validateHttpsUrl(process.env.ANNOUNCEMENT_WORKFLOW_URL, 'ANNOUNCEMENT_WORKFLOW_URL'),
    inline: false,
  });
}

for (let index = 0; index < chunks.length; index += 1) {
  const suffix = chunks.length > 1 ? ' (' + (index + 1) + '/' + chunks.length + ')' : '';
  const payload = {
    username: 'ChaptarrNG Releases',
    allowed_mentions: { parse: [] },
    embeds: [
      {
        title: (title + suffix).slice(0, 256),
        url: destination,
        description: chunks[index],
        color: 5814783,
        fields: index === 0 ? fields : [],
      },
    ],
  };

  let response;
  try {
    response = await fetch(webhookUrl, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
      redirect: 'error',
    });
  } catch {
    throw new Error('Discord webhook request failed before receiving a response.');
  }

  if (!response.ok) {
    throw new Error('Discord webhook returned HTTP ' + response.status + '.');
  }

  let receipt;
  try {
    receipt = await response.json();
  } catch {
    throw new Error('Discord webhook returned an unreadable response.');
  }
  if (
    !receipt.id ||
    receipt.embeds?.[0]?.description !== chunks[index]
  ) {
    throw new Error('Discord did not confirm the announcement message.');
  }

  if (index + 1 < chunks.length) {
    await sleep(1000);
  }
}

console.log('Posted ' + chunks.length + ' Discord announcement message(s).');

