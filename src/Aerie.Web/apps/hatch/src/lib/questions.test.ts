import { describe, expect, it } from 'vitest';
import { openQuestions, threadQuestions } from './questions';
import type { Comment, CommentKind } from '../types';

let next = 1;

const comment = (kind: CommentKind, body: string, answersId: number | null = null): Comment => ({
  id: next++,
  author: kind === 'answer' ? 'operator' : 'hatch-agent',
  body,
  kind,
  answersId,
  createdAt: '2026-09-05T00:00:00Z',
});

describe('threadQuestions', () => {
  it('finds nothing in a thread of ordinary comments', () => {
    expect(threadQuestions([comment('', 'sha abc123'), comment('', 'pushed')])).toEqual([]);
  });

  it('hangs an answer under the question it names', () => {
    const question = comment('question', 'per-node or global?');
    const answer = comment('answer', 'per-node', question.id);

    const threads = threadQuestions([question, comment('', 'unrelated'), answer]);

    expect(threads).toHaveLength(1);
    expect(threads[0].question.body).toBe('per-node or global?');
    expect(threads[0].answers.map((a) => a.body)).toEqual(['per-node']);
  });

  it('keeps every answer to one question, in the order they were given', () => {
    const question = comment('question', 'which disk?');
    const first = comment('answer', 'the 1200 GiB one', question.id);
    const second = comment('answer', 'on reflection, the reserved one', question.id);

    const [thread] = threadQuestions([question, first, second]);

    expect(thread.answers.map((a) => a.body)).toEqual(['the 1200 GiB one', 'on reflection, the reserved one']);
  });

  it('keeps two questions apart', () => {
    const one = comment('question', 'first');
    const two = comment('question', 'second');
    const answer = comment('answer', 'about the second', two.id);

    const threads = threadQuestions([one, two, answer]);

    expect(threads.map((t) => t.question.body)).toEqual(['first', 'second']);
    expect(threads[0].answers).toEqual([]);
    expect(threads[1].answers).toHaveLength(1);
  });

  /* An answer whose question is not in the list - a row hand-written, or a
     comment list that arrived truncated. It is dropped rather than promoted to
     a thread of its own: an answer with no question is not something the page
     can render, and inventing a question to hang it under would be a lie. */
  it('drops an answer pointing at a question that is not here', () => {
    expect(threadQuestions([comment('answer', 'to nothing', 9999)])).toEqual([]);
  });
});

describe('openQuestions', () => {
  it('is the questions with no answers', () => {
    const answered = comment('question', 'answered');
    const open = comment('question', 'open');

    const result = openQuestions([answered, comment('answer', 'yes', answered.id), open]);

    expect(result.map((t) => t.question.body)).toEqual(['open']);
  });

  it('is empty when everything has been decided', () => {
    const question = comment('question', 'decided');
    expect(openQuestions([question, comment('answer', 'yes', question.id)])).toEqual([]);
  });
});
