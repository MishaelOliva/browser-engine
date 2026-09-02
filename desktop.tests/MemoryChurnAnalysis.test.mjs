#!/usr/bin/env node

import assert from 'node:assert/strict';
import {
  analyzeChurnMemory,
  linearSlope,
  longestIncreasingRun
} from './MemoryChurnAnalysis.mjs';

const MEBIBYTE = 1024 * 1024;
const sample = ({
  privateMiB,
  workingSetMiB = privateMiB + 30,
  processCount = 6,
  handles = 320,
  threads = 72
}) => ({
  total: {
    privateBytes: privateMiB * MEBIBYTE,
    workingSetBytes: workingSetMiB * MEBIBYTE,
    processCount,
    handles,
    threads
  }
});

assert.equal(linearSlope([10, 20, 30, 40]), 10);
assert.equal(linearSlope([40, 30, 20, 10]), -10);
assert.equal(longestIncreasingRun([10, 20, 30, 25, 40, 50], 1), 2);

const plateau = analyzeChurnMemory({
  baselineSample: sample({ privateMiB: 220, handles: 300, threads: 70 }),
  cycleSamples: [245, 270, 282, 276, 284, 281].map(privateMiB => sample({
    privateMiB,
    handles: 390,
    threads: 86
  })),
  cooldownSamples: [260, 246, 240, 238, 241].map(privateMiB => sample({
    privateMiB,
    handles: 326,
    threads: 74
  }))
});
assert.equal(plateau.passed, true);
assert.equal(plateau.violations.length, 0);
assert.equal(plateau.evidence.suspiciousMonotonicGrowth, false);
assert.ok(plateau.evidence.retainedPrivateMiB <= 96);

const leak = analyzeChurnMemory({
  baselineSample: sample({ privateMiB: 200, handles: 250, threads: 60 }),
  cycleSamples: [225, 255, 290, 330, 375, 425].map((privateMiB, index) => sample({
    privateMiB,
    handles: 300 + (index * 120),
    threads: 70 + (index * 8)
  })),
  cooldownSamples: [410, 420, 435, 450].map((privateMiB, index) => sample({
    privateMiB,
    processCount: 10,
    handles: 850 + (index * 20),
    threads: 120
  })),
  limits: {
    maxRetainedPrivateMiB: 80,
    maxRetainedWorkingSetMiB: 100,
    maxCycleSlopePrivateMiB: 20,
    monotonicNoiseMiB: 2,
    maxMonotonicRun: 4,
    monotonicGrowthFloorMiB: 50,
    plateauToleranceMiB: 20,
    maxFinalProcessDelta: 1,
    maxFinalHandleDelta: 200,
    maxFinalThreadDelta: 20
  }
});
assert.equal(leak.passed, false);
assert.equal(leak.evidence.suspiciousMonotonicGrowth, true);
assert.ok(leak.violations.some(item => item.name === 'churn-retained-private-mib'));
assert.ok(leak.violations.some(item => item.name === 'churn-private-slope-mib-per-cycle'));
assert.ok(leak.violations.some(item => item.name === 'churn-monotonic-growth-run'));
assert.ok(leak.violations.some(item => item.name === 'churn-cooldown-plateau-range-mib'));
assert.ok(leak.violations.some(item => item.name === 'churn-final-process-delta'));

const noisyCleanup = analyzeChurnMemory({
  baselineSample: sample({ privateMiB: 180 }),
  cycleSamples: [220, 235, 228, 242, 231, 239].map(privateMiB => sample({ privateMiB })),
  cooldownSamples: [230, 205, 194, 198].map(privateMiB => sample({ privateMiB })),
  limits: { plateauToleranceMiB: 40 }
});
assert.equal(noisyCleanup.passed, true);

assert.throws(
  () => analyzeChurnMemory({
    baselineSample: sample({ privateMiB: 100 }),
    cycleSamples: [sample({ privateMiB: 110 }), sample({ privateMiB: 120 })],
    cooldownSamples: [sample({ privateMiB: 105 }), sample({ privateMiB: 104 })]
  }),
  /at least three completed cycles/);
assert.throws(
  () => analyzeChurnMemory({
    baselineSample: sample({ privateMiB: 100 }),
    cycleSamples: [110, 115, 120].map(privateMiB => sample({ privateMiB })),
    cooldownSamples: []
  }),
  /at least two samples/);

console.log(JSON.stringify({
  status: 'PASS',
  contract: 'bounded churn detects retained monotonic growth and accepts cleanup plateaus'
}));

