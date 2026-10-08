import { randomUUID } from 'node:crypto';
import { DOMParser, XMLSerializer } from '@xmldom/xmldom';

function parseDocument(xml) {
  if (typeof xml !== 'string' || !xml.trim()) return null;
  try {
    const document = new DOMParser({
      onError: () => {
        throw new TypeError('Invalid call XML');
      }
    }).parseFromString(xml, 'application/xml');
    if (
      document.doctype ||
      !document.documentElement ||
      Array.from(document.childNodes).some((node) => node.nodeType === 3 && node.data.trim())
    ) {
      return null;
    }
    return document;
  } catch {
    return null;
  }
}

function isElement(node, name) {
  return node.nodeType === 1 && node.tagName === name && !node.namespaceURI;
}

export function isVoiceResponse(xml) {
  const document = parseDocument(xml);
  return Boolean(
    document && isElement(document.documentElement, 'Response') && !xml.includes('{{')
  );
}

export function prepareOpeningStep(twiML) {
  return prepareStep(twiML);
}

export function prepareStep(twiML, selectedStepId, expectedInitialStep) {
  const document = parseDocument(twiML);
  if (!document || !isElement(document.documentElement, 'CallFlow')) return null;
  const root = document.documentElement;
  const initialStep = root.getAttribute('initialStep');
  if (!initialStep || !initialStep.trim() || initialStep.length > 200) return null;
  if (expectedInitialStep !== undefined && initialStep !== expectedInitialStep) return null;
  const stepId = selectedStepId ?? initialStep;
  if (!stepId || !stepId.trim() || stepId.length > 200) return null;
  const steps = Array.from(root.childNodes).filter(
    (node) => isElement(node, 'Step') && node.getAttribute('id') === stepId
  );
  if (steps.length !== 1) return null;
  const responses = Array.from(steps[0].childNodes).filter((node) => isElement(node, 'Response'));
  if (responses.length !== 1) return null;
  const executionId = randomUUID();
  const responseTwiML = new XMLSerializer()
    .serializeToString(responses[0])
    .replaceAll('{{executionId}}', executionId);
  if (responseTwiML.includes('{{')) return null;
  return { stepId, executionId, responseTwiML, expectedTwiML: twiML };
}
