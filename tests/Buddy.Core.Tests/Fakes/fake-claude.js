// Stand-in for the Claude Code CLI in headless streaming mode, used by ClaudeProcessTests.
// For each user message: emits init, asks permission for a Write, then reports the decision as text.
const readline = require('readline');
const rl = readline.createInterface({ input: process.stdin });
const out = (obj) => process.stdout.write(JSON.stringify(obj) + '\n');
let pending = null;

out({ type: 'system', subtype: 'init', session_id: 'fake-session', model: 'fake' });

rl.on('line', (line) => {
  const msg = JSON.parse(line);
  if (msg.type === 'user') {
    pending = 'req-1';
    out({ type: 'control_request', request_id: pending, request: { subtype: 'can_use_tool', tool_name: 'Write', input: { file_path: 'hello.txt', content: msg.message.content } } });
  } else if (msg.type === 'control_response' && msg.response.request_id === pending) {
    const r = msg.response.response;
    const text = r.behavior === 'allow' ? `scritto: ${r.updatedInput.content}` : `negato: ${r.message}`;
    out({ type: 'stream_event', event: { type: 'content_block_delta', delta: { type: 'text_delta', text } } });
    out({ type: 'assistant', message: { content: [{ type: 'text', text }] } });
    out({ type: 'result', is_error: false, result: text, total_cost_usd: 0, session_id: 'fake-session' });
  }
});
