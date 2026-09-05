/* Threading a comment list into questions and their answers.

   The server has the same notion (Modules/Hatch/Questions.cs) and the page
   could ask it for one. It does not, because the issue page already fetches
   every comment on the issue and a question is one of them - a second request
   for rows the browser is holding would be two sources for one truth, and the
   day they disagreed the page would show a question as open while the board
   showed it answered.

   "Open" is the same definition in both places and it is the only one worth
   having: a question with nothing pointing at it. Never a flag, so it cannot be
   set wrong. */

import type { Comment } from '../types';

export interface QuestionThread {
  question: Comment;
  /** Oldest first. Empty is what open means. */
  answers: Comment[];
}

/** Every question on the issue, oldest first, each with what was said back. */
export function threadQuestions(comments: Comment[]): QuestionThread[] {
  const answers = new Map<number, Comment[]>();
  for (const comment of comments) {
    if (comment.kind !== 'answer' || comment.answersId === null) continue;
    const found = answers.get(comment.answersId);
    if (found) found.push(comment);
    else answers.set(comment.answersId, [comment]);
  }

  return comments
    .filter((c) => c.kind === 'question')
    .map((question) => ({ question, answers: answers.get(question.id) ?? [] }));
}

/** The ones nobody has answered - what a person is being asked to decide. */
export const openQuestions = (comments: Comment[]): QuestionThread[] =>
  threadQuestions(comments).filter((thread) => thread.answers.length === 0);
