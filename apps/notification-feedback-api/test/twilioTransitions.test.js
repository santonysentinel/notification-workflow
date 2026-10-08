import { describe, it } from 'mocha';
import { expect } from 'chai';
import { readFileSync } from 'node:fs';
import { prepareNextStep } from '../app/utils/twilioTransitions.js';

describe('Twilio transition selection', () => {
  const flow = {
    schemaVersion: 1,
    initialStep: 'reminder',
    steps: [
      { id: 'reminder', type: 'redirect', transitions: { next: 'question' } },
      {
        id: 'question',
        type: 'gather',
        transitions: {
          digits: { 1: 'yes', 2: 'no' },
          speech: { yes: 'yes', 'not now': 'no' },
          noInput: 'silent',
          fallback: 'unknown'
        }
      },
      ...['yes', 'no', 'silent', 'unknown'].map((id) => ({ id, type: 'terminal' }))
    ]
  };
  const bundle =
    '<CallFlow initialStep="reminder">' +
    flow.steps
      .map(
        ({ id }) =>
          `<Step id="${id}"><Response><Say>${id}</Say><Redirect>https://example.com/next?executionId={{executionId}}</Redirect></Response></Step>`
      )
      .join('') +
    '</CallFlow>';
  const templateJSON = JSON.stringify(flow);

  for (const [source, input, target, inputType] of [
    ['reminder', {}, 'question', 'redirect'],
    ['question', { digits: '1' }, 'yes', 'dtmf'],
    ['question', { digits: '2', speechResult: 'yes' }, 'no', 'dtmf'],
    ['question', { speechResult: ' YES ' }, 'yes', 'speech'],
    ['question', { speechResult: 'Not   Now' }, 'no', 'speech'],
    ['question', {}, 'silent', 'no-input'],
    ['question', { digits: '', speechResult: '  ' }, 'silent', 'no-input'],
    ['question', { digits: '9' }, 'unknown', 'dtmf'],
    ['question', { speechResult: 'maybe' }, 'unknown', 'speech']
  ]) {
    it(`selects ${target} for ${source} with ${JSON.stringify(input)}`, () => {
      const candidate = prepareNextStep(bundle, templateJSON, source, input);
      expect(candidate.stepId).to.equal(target);
      expect(candidate.sourceStepId).to.equal(source);
      expect(candidate.inputType).to.equal(inputType);
      expect(candidate.expectedTemplateJSON).to.equal(templateJSON);
      expect(candidate.responseTwiML).to.include(`<Say>${target}</Say>`);
      expect(candidate.responseTwiML).to.include(candidate.executionId);
      expect(candidate.responseTwiML).not.to.include('{{');
    });
  }

  it('rejects unknown and terminal source steps', () => {
    for (const source of ['missing', 'yes'])
      expect(prepareNextStep(bundle, templateJSON, source, {})).to.equal(null);
  });

  for (const [label, mutate] of [
    [
      'unsupported version',
      (copy) => {
        copy.schemaVersion = 2;
      }
    ],
    [
      'duplicate IDs',
      (copy) => {
        copy.steps.push(copy.steps[0]);
      }
    ],
    [
      'missing destination',
      (copy) => {
        copy.steps[0].transitions.next = 'missing';
      }
    ],
    [
      'unsupported business action',
      (copy) => {
        copy.steps[0].type = 'action';
      }
    ],
    [
      'embedded actions',
      (copy) => {
        copy.steps[0].actions = ['acknowledge'];
      }
    ],
    [
      'missing no-input rule',
      (copy) => {
        delete copy.steps[1].transitions.noInput;
      }
    ],
    [
      'missing fallback',
      (copy) => {
        delete copy.steps[1].transitions.fallback;
      }
    ],
    [
      'ambiguous normalized speech',
      (copy) => {
        copy.steps[1].transitions.speech.YES = 'no';
      }
    ]
  ]) {
    it(`rejects ${label}`, () => {
      const copy = structuredClone(flow);
      mutate(copy);
      expect(prepareNextStep(bundle, JSON.stringify(copy), 'reminder', {})).to.equal(null);
    });
  }

  it('rejects malformed graphs, missing responses, and mismatched initial steps', () => {
    expect(prepareNextStep(bundle, 'not JSON', 'reminder', {})).to.equal(null);
    expect(
      prepareNextStep(bundle.replace('id="question"', 'id="other"'), templateJSON, 'reminder', {})
    ).to.equal(null);
    expect(
      prepareNextStep(
        bundle.replace('initialStep="reminder"', 'initialStep="question"'),
        templateJSON,
        'reminder',
        {}
      )
    ).to.equal(null);
  });

  it('executes every branch in the documented complete TemplateJSON/XML example', () => {
    const document = readFileSync(
      new URL('../../../docs/twilio_flow_contract.md', import.meta.url),
      'utf8'
    );
    const graph = document.match(/```json\r?\n([\s\S]*?)\r?\n```/)[1];
    const xml = document.match(/```xml\r?\n([\s\S]*?)\r?\n```/)[1];
    expect(prepareNextStep(xml, graph, 'battery-reminder', {}).stepId).to.equal('acknowledgement');
    for (const [input, target] of [
      [{ digits: '1' }, 'acknowledged'],
      [{ digits: '2' }, 'declined'],
      [{ speechResult: 'YES' }, 'acknowledged'],
      [{ speechResult: 'not  now' }, 'declined'],
      [{}, 'no-input'],
      [{ digits: '9' }, 'unrecognized']
    ]) {
      const candidate = prepareNextStep(xml, graph, 'acknowledgement', input);
      expect(candidate.stepId).to.equal(target);
      expect(candidate.responseTwiML).to.include('<Hangup/>');
    }
  });
});
