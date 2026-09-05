import { describe, expect, it } from 'vitest';
import { emptyMessage, readEvents, visibleCandidates, type Candidate } from './logic';
const items: Candidate[] = [
  { id: '1', name: '双语', languages: ['zh', 'en'], chinese: true, format: 'ass', score: 1 },
  { id: '2', name: 'English', languages: ['en'], chinese: false, format: 'srt', score: 2 }
];
describe('候选与流式反馈', () => {
  it('默认中文，也可以看到全部且不自动选择', () => {
    expect(visibleCandidates(items, true).map(item => item.id)).toEqual(['1']);
    expect(visibleCandidates(items, false)).toEqual(items);
    expect(emptyMessage([items[1]], true)).toContain('另有 1 条');
    expect(emptyMessage([], true)).toContain('暂时没有');
  });
  it('跨网络分块和 UTF-8 字节边界仍可接收中文事件', async () => {
    const data = new TextEncoder().encode('{"type":"progress","phase":"hashing"}\n{"type":"error","message":"网络失败"}\n');
    const stream = new ReadableStream<Uint8Array>({ start(controller) { for (const byte of data) controller.enqueue(Uint8Array.of(byte)); controller.close(); } });
    const events: Record<string, unknown>[] = [];
    await readEvents(stream, event => events.push(event));
    expect(events).toEqual([{ type: 'progress', phase: 'hashing' }, { type: 'error', message: '网络失败' }]);
  });
});
