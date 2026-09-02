const MEBIBYTE = 1024 * 1024;

export const DEFAULT_CHURN_LIMITS = Object.freeze({
  maxRetainedPrivateMiB: 96,
  maxRetainedWorkingSetMiB: 160,
  maxCycleSlopePrivateMiB: 24,
  monotonicNoiseMiB: 4,
  maxMonotonicRun: 4,
  monotonicGrowthFloorMiB: 64,
  plateauWindow: 4,
  plateauToleranceMiB: 48,
  maxFinalProcessDelta: 2,
  maxFinalHandleDelta: 512,
  maxFinalThreadDelta: 32
});

function finiteNumber(value, name) {
  const number = Number(value);
  if (!Number.isFinite(number)) throw new TypeError(`${name} must be finite`);
  return number;
}

function sampleMetric(sample, metric, name) {
  if (!sample || typeof sample !== 'object' || !sample.total || typeof sample.total !== 'object') {
    throw new TypeError(`${name} must contain a total aggregate`);
  }
  return finiteNumber(sample.total[metric], `${name}.total.${metric}`);
}

function normalizeLimits(overrides = {}) {
  const limits = { ...DEFAULT_CHURN_LIMITS, ...overrides };
  for (const [name, value] of Object.entries(limits)) {
    limits[name] = finiteNumber(value, name);
    if (limits[name] < 0) throw new RangeError(`${name} cannot be negative`);
  }
  limits.plateauWindow = Math.max(2, Math.trunc(limits.plateauWindow));
  limits.maxMonotonicRun = Math.max(2, Math.trunc(limits.maxMonotonicRun));
  return limits;
}

export function linearSlope(values) {
  if (!Array.isArray(values) || values.length < 2) return 0;
  const normalized = values.map((value, index) => finiteNumber(value, `values[${index}]`));
  const meanX = (normalized.length - 1) / 2;
  const meanY = normalized.reduce((sum, value) => sum + value, 0) / normalized.length;
  let numerator = 0;
  let denominator = 0;
  for (let index = 0; index < normalized.length; index += 1) {
    const x = index - meanX;
    numerator += x * (normalized[index] - meanY);
    denominator += x * x;
  }
  return denominator === 0 ? 0 : numerator / denominator;
}

export function longestIncreasingRun(values, noiseFloor = 0) {
  if (!Array.isArray(values) || values.length < 2) return 0;
  const noise = Math.max(0, finiteNumber(noiseFloor, 'noiseFloor'));
  let longest = 0;
  let current = 0;
  for (let index = 1; index < values.length; index += 1) {
    const previous = finiteNumber(values[index - 1], `values[${index - 1}]`);
    const next = finiteNumber(values[index], `values[${index}]`);
    if (next > previous + noise) {
      current += 1;
      longest = Math.max(longest, current);
    } else {
      current = 0;
    }
  }
  return longest;
}

function asMiB(bytes) {
  return Math.round((bytes / MEBIBYTE) * 10) / 10;
}

function check(name, actual, limit, passed) {
  return { name, actual, limit, passed: Boolean(passed) };
}

/**
 * Evaluates a completed baseline -> bounded tab churn -> cooldown trace.
 *
 * The limits are deliberately generous because Chromium keeps reusable heaps
 * and processes. Regressions fail on retained post-cleanup growth, sustained
 * per-cycle slope, a suspicious monotonic run, missing cooldown plateau, or
 * process/handle/thread resources that do not return near baseline.
 */
export function analyzeChurnMemory({
  baselineSample,
  cycleSamples,
  cooldownSamples,
  limits: limitOverrides = {}
}) {
  if (!Array.isArray(cycleSamples) || cycleSamples.length < 3) {
    throw new RangeError('cycleSamples must contain at least three completed cycles');
  }
  if (!Array.isArray(cooldownSamples) || cooldownSamples.length < 2) {
    throw new RangeError('cooldownSamples must contain at least two samples');
  }

  const limits = normalizeLimits(limitOverrides);
  const baselinePrivate = sampleMetric(baselineSample, 'privateBytes', 'baselineSample');
  const baselineWorkingSet = sampleMetric(baselineSample, 'workingSetBytes', 'baselineSample');
  const baselineProcesses = sampleMetric(baselineSample, 'processCount', 'baselineSample');
  const baselineHandles = sampleMetric(baselineSample, 'handles', 'baselineSample');
  const baselineThreads = sampleMetric(baselineSample, 'threads', 'baselineSample');
  const cyclePrivate = cycleSamples.map((sample, index) =>
    sampleMetric(sample, 'privateBytes', `cycleSamples[${index}]`));
  const finalSample = cooldownSamples.at(-1);
  const finalPrivate = sampleMetric(finalSample, 'privateBytes', 'finalCooldownSample');
  const finalWorkingSet = sampleMetric(finalSample, 'workingSetBytes', 'finalCooldownSample');
  const finalProcesses = sampleMetric(finalSample, 'processCount', 'finalCooldownSample');
  const finalHandles = sampleMetric(finalSample, 'handles', 'finalCooldownSample');
  const finalThreads = sampleMetric(finalSample, 'threads', 'finalCooldownSample');

  const plateauSamples = cooldownSamples.slice(-Math.min(limits.plateauWindow, cooldownSamples.length));
  const plateauPrivate = plateauSamples.map((sample, index) =>
    sampleMetric(sample, 'privateBytes', `plateauSamples[${index}]`));
  const plateauRange = Math.max(...plateauPrivate) - Math.min(...plateauPrivate);
  const plateauSlope = linearSlope(plateauPrivate);
  const cycleSlope = linearSlope(cyclePrivate);
  const monotonicRun = longestIncreasingRun(cyclePrivate, limits.monotonicNoiseMiB * MEBIBYTE);
  const cycleGrowth = cyclePrivate.at(-1) - cyclePrivate[0];
  const retainedPrivate = finalPrivate - baselinePrivate;
  const retainedWorkingSet = finalWorkingSet - baselineWorkingSet;
  const suspiciousMonotonicGrowth = monotonicRun >= limits.maxMonotonicRun
    && cycleGrowth > limits.monotonicGrowthFloorMiB * MEBIBYTE;

  const checks = [
    check(
      'churn-retained-private-mib',
      asMiB(retainedPrivate),
      limits.maxRetainedPrivateMiB,
      retainedPrivate <= limits.maxRetainedPrivateMiB * MEBIBYTE),
    check(
      'churn-retained-working-set-mib',
      asMiB(retainedWorkingSet),
      limits.maxRetainedWorkingSetMiB,
      retainedWorkingSet <= limits.maxRetainedWorkingSetMiB * MEBIBYTE),
    check(
      'churn-private-slope-mib-per-cycle',
      asMiB(cycleSlope),
      limits.maxCycleSlopePrivateMiB,
      cycleSlope <= limits.maxCycleSlopePrivateMiB * MEBIBYTE),
    check(
      'churn-monotonic-growth-run',
      monotonicRun,
      `< ${limits.maxMonotonicRun} when growth exceeds ${limits.monotonicGrowthFloorMiB} MiB`,
      !suspiciousMonotonicGrowth),
    check(
      'churn-cooldown-plateau-range-mib',
      asMiB(plateauRange),
      limits.plateauToleranceMiB,
      plateauRange <= limits.plateauToleranceMiB * MEBIBYTE),
    check(
      'churn-final-process-delta',
      finalProcesses - baselineProcesses,
      limits.maxFinalProcessDelta,
      finalProcesses - baselineProcesses <= limits.maxFinalProcessDelta),
    check(
      'churn-final-handle-delta',
      finalHandles - baselineHandles,
      limits.maxFinalHandleDelta,
      finalHandles - baselineHandles <= limits.maxFinalHandleDelta),
    check(
      'churn-final-thread-delta',
      finalThreads - baselineThreads,
      limits.maxFinalThreadDelta,
      finalThreads - baselineThreads <= limits.maxFinalThreadDelta)
  ];

  return {
    limits,
    checks,
    violations: checks.filter(item => !item.passed),
    passed: checks.every(item => item.passed),
    evidence: {
      cycles: cycleSamples.length,
      cooldownSamples: cooldownSamples.length,
      retainedPrivateMiB: asMiB(retainedPrivate),
      retainedWorkingSetMiB: asMiB(retainedWorkingSet),
      cycleSlopePrivateMiB: asMiB(cycleSlope),
      cycleGrowthPrivateMiB: asMiB(cycleGrowth),
      longestMonotonicGrowthRun: monotonicRun,
      suspiciousMonotonicGrowth,
      plateauWindow: plateauSamples.length,
      plateauRangePrivateMiB: asMiB(plateauRange),
      plateauSlopePrivateMiB: asMiB(plateauSlope),
      finalProcessDelta: finalProcesses - baselineProcesses,
      finalHandleDelta: finalHandles - baselineHandles,
      finalThreadDelta: finalThreads - baselineThreads
    }
  };
}

