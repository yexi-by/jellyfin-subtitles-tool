export interface Candidate { id: string; name: string; format: string; languages: string[]; score: number; chinese: boolean }
export function visibleCandidates(candidates: Candidate[], chineseOnly: boolean): Candidate[] {
  return chineseOnly ? candidates.filter(candidate => candidate.chinese) : candidates;
}
export function emptyMessage(candidates: Candidate[], chineseOnly: boolean): string {
  return candidates.length && chineseOnly ? `没有中文字幕，另有 ${candidates.length} 条其他语言或未标注语言的结果。` : '字幕源暂时没有这个视频的字幕。';
}
export async function readEvents(body: ReadableStream<Uint8Array>, receive: (event: Record<string, unknown>) => void): Promise<void> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  try {
    while (true) {
      const { done, value } = await reader.read();
      buffer += done ? decoder.decode() : decoder.decode(value, { stream: true });
      let end: number;
      while ((end = buffer.indexOf('\n')) !== -1) {
        const line = buffer.slice(0, end).trim();
        buffer = buffer.slice(end + 1);
        if (line) receive(JSON.parse(line));
      }
      if (done) break;
    }
    if (buffer.trim()) receive(JSON.parse(buffer));
  } finally { reader.releaseLock(); }
}
