import { prepareStep } from './twilioTwiML.js';

const isObject = (value) => value !== null && typeof value === 'object' && !Array.isArray(value);
const validId = (value) => typeof value === 'string' && value.trim() && value.length <= 200;
const normalizeSpeech = (value) => value.trim().toLowerCase().replace(/\s+/g, ' ');

function parseFlow(templateJSON) {
  const flow = JSON.parse(templateJSON);
  if (
    !isObject(flow) ||
    flow.schemaVersion !== 1 ||
    !validId(flow.initialStep) ||
    !Array.isArray(flow.steps) ||
    !flow.steps.length ||
    Object.hasOwn(flow, 'actions')
  )
    return null;
  const steps = new Map();
  for (const step of flow.steps) {
    if (
      !isObject(step) ||
      !validId(step.id) ||
      steps.has(step.id) ||
      !['redirect', 'gather', 'terminal'].includes(step.type) ||
      Object.hasOwn(step, 'actions')
    )
      return null;
    steps.set(step.id, step);
  }
  if (!steps.has(flow.initialStep)) return null;
  for (const step of steps.values()) {
    const transitions = step.transitions;
    if (step.type === 'terminal') {
      if (transitions !== undefined && (!isObject(transitions) || Object.keys(transitions).length))
        return null;
      continue;
    }
    if (!isObject(transitions)) return null;
    const allowed =
      step.type === 'redirect' ? ['next'] : ['digits', 'speech', 'noInput', 'fallback'];
    if (Object.keys(transitions).some((key) => !allowed.includes(key))) return null;
    const targets = [];
    if (step.type === 'redirect') {
      targets.push(transitions.next);
    } else {
      targets.push(transitions.noInput, transitions.fallback);
      for (const kind of ['digits', 'speech']) {
        if (transitions[kind] === undefined) continue;
        if (!isObject(transitions[kind])) return null;
        const keys = new Set();
        for (const [key, target] of Object.entries(transitions[kind])) {
          const normalized = kind === 'speech' ? normalizeSpeech(key) : key;
          if (!normalized || (kind === 'digits' && !/^[0-9*#]+$/.test(key)) || keys.has(normalized))
            return null;
          keys.add(normalized);
          targets.push(target);
        }
      }
    }
    if (targets.some((target) => !validId(target) || !steps.has(target))) return null;
  }
  return { flow, steps };
}

export function prepareNextStep(twiML, templateJSON, sourceStepId, input) {
  try {
    const parsed = parseFlow(templateJSON);
    if (!parsed) return null;
    const step = parsed.steps.get(sourceStepId);
    if (!step || step.type === 'terminal') return null;
    let target;
    let inputType;
    if (step.type === 'redirect') {
      target = step.transitions.next;
      inputType = 'redirect';
    } else if (input.digits) {
      inputType = 'dtmf';
      const matches = step.transitions.digits ?? {};
      target = Object.hasOwn(matches, input.digits)
        ? matches[input.digits]
        : step.transitions.fallback;
    } else if (input.speechResult?.trim()) {
      inputType = 'speech';
      const speech = normalizeSpeech(input.speechResult);
      const match = Object.entries(step.transitions.speech ?? {}).find(
        ([key]) => normalizeSpeech(key) === speech
      );
      target = match ? match[1] : step.transitions.fallback;
    } else {
      inputType = 'no-input';
      target = step.transitions.noInput;
    }
    const candidate = prepareStep(twiML, target, parsed.flow.initialStep);
    return candidate
      ? { ...candidate, expectedTemplateJSON: templateJSON, sourceStepId, inputType }
      : null;
  } catch {
    return null;
  }
}
