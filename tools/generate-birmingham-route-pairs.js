#!/usr/bin/env node
const fs = require('fs');

const output = process.env.OUTPUT || 'TestResults/accesscity-production-soak/birmingham-1000-routes.json';
const count = Number(process.env.ROUTE_COUNT || 1000);

const anchors = [
  ['new-street', -1.8988, 52.4778],
  ['bullring', -1.8936, 52.4770],
  ['victoria-square', -1.9026, 52.4797],
  ['colmore', -1.8993, 52.4833],
  ['brindleyplace', -1.9146, 52.4770],
  ['library', -1.9086, 52.4797],
  ['snow-hill', -1.8991, 52.4836],
  ['cathedral', -1.8995, 52.4812],
  ['digbeth', -1.8844, 52.4756],
  ['jewellery-quarter', -1.9124, 52.4896],
  ['five-ways', -1.9121, 52.4710],
  ['aston-university', -1.8888, 52.4865],
  ['edgbaston', -1.9222, 52.4600],
  ['moor-street', -1.8907, 52.4790],
  ['mailbox', -1.9057, 52.4759],
  ['broad-street', -1.9125, 52.4766],
];

const profiles = [
  ['standard', 0.45, []],
  ['manual-wheelchair', 0.75, ['avoid-stairs', 'wheelchair']],
  ['stroller', 0.65, ['avoid-stairs', 'avoid-cobblestone']],
  ['standard', 0.70, ['avoid-reported-hazards']],
];

function jitter(index, salt, width = 0.0022) {
  const x = Math.sin((index + 1) * (salt + 3) * 12.9898) * 43758.5453;
  return (x - Math.floor(x) - 0.5) * width;
}

const routes = [];
for (let i = 0; i < count; i++) {
  const a = anchors[i % anchors.length];
  const b = anchors[(i * 7 + 3) % anchors.length];
  const profile = profiles[i % profiles.length];
  routes.push({
    name: `${a[0]}-to-${b[0]}-${i}`,
    start: {
      x: Number((a[1] + jitter(i, 1)).toFixed(7)),
      y: Number((a[2] + jitter(i, 2)).toFixed(7)),
    },
    end: {
      x: Number((b[1] + jitter(i, 3)).toFixed(7)),
      y: Number((b[2] + jitter(i, 4)).toFixed(7)),
    },
    profile: profile[0],
    safetyWeight: profile[1],
    preferences: profile[2],
  });
}

fs.mkdirSync(require('path').dirname(output), { recursive: true });
fs.writeFileSync(output, `${JSON.stringify({
  name: `birmingham-${count}-route-production-soak`,
  generatedAtUtc: new Date().toISOString(),
  routes,
}, null, 2)}\n`);
console.log(output);
