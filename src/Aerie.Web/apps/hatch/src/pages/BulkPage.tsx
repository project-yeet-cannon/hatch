import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Button, Card, EmptyState, Field, PageHeader, Table } from '@aerie/ui';
import { bulkEditIssues, getBoard, getProjects, searchIssues } from '../api/client';
import { MomentField } from '../components/MomentField';
import { StatusPill } from '../components/StatusPill';
import { TypeBadge } from '../components/TypeBadge';
import { EMPTY_FORM, KEEP, buildBulkEdit, isEmptyForm, summarize } from '../lib/bulk';
import type { BulkForm } from '../lib/bulk';
import { message } from '../lib/errors';
import { truncate } from '../lib/text';
import { ISSUE_TYPES } from '../types';
import type { Board, IssueBulkResult, IssueCard, IssueSearch, IssueType, Project } from '../types';

/** The filter, before it is turned into a query. '' is "any", which is not a value the API takes. */
interface Filters {
  projectId: number | '';
  type: IssueType | '';
  statusId: number | '';
  parentKey: string;
  ancestorKey: string;
  text: string;
}

const NO_FILTERS: Filters = { projectId: '', type: '', statusId: '', parentKey: KEEP, ancestorKey: '', text: '' };

/**
 * Find some issues, change one thing about all of them.
 *
 * Two halves, in the order they are used: a filter that runs server-side (the
 * ancestor walk is not something the browser could do from a board payload) and
 * an edit applied to whichever of the results are still ticked.
 *
 * The results are ticked, rather than the filter being re-run at apply time, on
 * purpose: the request names keys, so what gets edited is exactly what was on
 * screen when the button was pressed. An issue filed in between - by the other
 * person, or by an agent - cannot be swept into an edit nobody saw.
 */
export function BulkPage() {
  const [board, setBoard] = useState<Board | null>(null);
  const [projects, setProjects] = useState<Project[]>([]);

  const [filters, setFilters] = useState<Filters>(NO_FILTERS);
  const [results, setResults] = useState<IssueCard[] | null>(null);
  const [chosen, setChosen] = useState<Set<string>>(new Set());
  const [searching, setSearching] = useState(false);

  const [form, setForm] = useState<BulkForm>(EMPTY_FORM);
  const [applying, setApplying] = useState(false);
  const [outcome, setOutcome] = useState<IssueBulkResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    Promise.all([getBoard(), getProjects()])
      .then(([loadedBoard, loadedProjects]) => {
        setBoard(loadedBoard);
        setProjects(loadedProjects);
      })
      .catch((err: unknown) => setError(message(err)));
  }, []);

  const search = useCallback(async () => {
    setSearching(true);
    setOutcome(null);
    try {
      const query: IssueSearch = {
        projectId: filters.projectId === '' ? null : filters.projectId,
        type: filters.type === '' ? null : filters.type,
        statusId: filters.statusId === '' ? null : filters.statusId,
        // KEEP is "any parent"; the empty string is a filter of its own -
        // "no parent at all" - and has to reach the API as one.
        parentKey: filters.parentKey === KEEP ? null : filters.parentKey,
        ancestorKey: filters.ancestorKey || null,
        text: filters.text.trim() || null,
      };

      const found = await searchIssues(query);
      setResults(found);
      // Everything found starts ticked: the filter is the selection, and the
      // ticks are there to take things back out of it.
      setChosen(new Set(found.map((i) => i.key)));
      setError(null);
    } catch (err) {
      setError(message(err));
    } finally {
      setSearching(false);
    }
  }, [filters]);

  async function apply() {
    const request = buildBulkEdit([...chosen], form);
    if (!request) return;

    setApplying(true);
    try {
      const result = await bulkEditIssues(request);
      setOutcome(result);
      setError(null);
      // Re-run the filter rather than patching the rows in place: an edit that
      // moved issues out of the filter should take them off this list, and the
      // server is the one that knows what still matches.
      await search();
    } catch (err) {
      setError(message(err));
    } finally {
      setApplying(false);
    }
  }

  if (!board) return <p className="text-muted">{error ?? 'Loading…'}</p>;

  const statuses = board.statuses;
  const issues = board.issues;
  const request = buildBulkEdit([...chosen], form);

  return (
    <div className="hatch-page hatch-bulk-page">
      <PageHeader
        title="Bulk edit"
        description="Find issues, then change one thing about all of them at once."
      />

      {error && <p className="text-danger">{error}</p>}

      <Card>
        <h2 className="hatch-section-title">Find</h2>
        <div className="hatch-field-grid">
          <Field label="Project">
            <select
              value={filters.projectId}
              onChange={(e) => setFilters({ ...filters, projectId: e.target.value === '' ? '' : Number(e.target.value) })}
            >
              <option value="">— any —</option>
              {projects.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.key} — {p.name}
                </option>
              ))}
            </select>
          </Field>

          <Field label="Type">
            <select value={filters.type} onChange={(e) => setFilters({ ...filters, type: e.target.value as IssueType | '' })}>
              <option value="">— any —</option>
              {ISSUE_TYPES.map((t) => (
                <option key={t} value={t}>
                  {t}
                </option>
              ))}
            </select>
          </Field>

          <Field label="Status">
            <select
              value={filters.statusId}
              onChange={(e) => setFilters({ ...filters, statusId: e.target.value === '' ? '' : Number(e.target.value) })}
            >
              <option value="">— any —</option>
              {statuses.map((s) => (
                <option key={s.id} value={s.id}>
                  {s.name}
                </option>
              ))}
            </select>
          </Field>

          <Field label="Parent" hint="The issue directly above these.">
            <select value={filters.parentKey} onChange={(e) => setFilters({ ...filters, parentKey: e.target.value })}>
              <option value={KEEP}>— any —</option>
              <option value="">— no parent —</option>
              {issues.map((i) => (
                <option key={i.key} value={i.key}>
                  {i.key} — {truncate(i.title, 60)}
                </option>
              ))}
            </select>
          </Field>

          <Field label="Ancestor" hint="Everything below it, at every level.">
            <select value={filters.ancestorKey} onChange={(e) => setFilters({ ...filters, ancestorKey: e.target.value })}>
              <option value="">— any —</option>
              {issues.map((i) => (
                <option key={i.key} value={i.key}>
                  {i.key} — {truncate(i.title, 60)}
                </option>
              ))}
            </select>
          </Field>

          <Field label="Title contains">
            <input value={filters.text} onChange={(e) => setFilters({ ...filters, text: e.target.value })} />
          </Field>
        </div>

        <div className="hatch-form-actions">
          <Button onClick={() => setFilters(NO_FILTERS)}>Reset</Button>
          <Button variant="primary" loading={searching} onClick={() => void search()}>
            Find issues
          </Button>
        </div>
      </Card>

      {results && results.length === 0 && <EmptyState message="Nothing matches that filter." />}

      {results && results.length > 0 && (
        <>
          <Card>
            <h2 className="hatch-section-title">Change</h2>
            <div className="hatch-field-grid">
              <Field label="Type">
                <select value={form.type} onChange={(e) => setForm({ ...form, type: e.target.value as IssueType | '' })}>
                  <option value="">— leave alone —</option>
                  {ISSUE_TYPES.map((t) => (
                    <option key={t} value={t}>
                      {t}
                    </option>
                  ))}
                </select>
              </Field>

              <Field label="Status">
                <select
                  value={form.statusId}
                  onChange={(e) => setForm({ ...form, statusId: e.target.value === '' ? '' : Number(e.target.value) })}
                >
                  <option value="">— leave alone —</option>
                  {statuses.map((s) => (
                    <option key={s.id} value={s.id}>
                      {s.name}
                    </option>
                  ))}
                </select>
              </Field>

              <Field label="Parent" hint="An issue and its parent share a project.">
                <select value={form.parent} onChange={(e) => setForm({ ...form, parent: e.target.value })}>
                  <option value={KEEP}>— leave alone —</option>
                  <option value="">— no parent —</option>
                  {issues.map((i) => (
                    <option key={i.key} value={i.key}>
                      {i.key} — {truncate(i.title, 60)}
                    </option>
                  ))}
                </select>
              </Field>

              {/* The tick is what separates "leave this alone" from "clear it" -
                  an empty date field cannot say both, and the difference is the
                  one that would quietly wipe a column of due dates. */}
              <div className="hatch-dated-field">
                <label className="hatch-change-toggle">
                  <input
                    type="checkbox"
                    checked={form.setReady}
                    onChange={(e) => setForm({ ...form, setReady: e.target.checked })}
                  />
                  Change the ready date
                </label>
                {form.setReady && (
                  <MomentField
                    label="Ready"
                    hint="Leave the date empty to take it off."
                    value={form.readyAt}
                    onChange={(readyAt) => setForm({ ...form, readyAt })}
                  />
                )}
              </div>

              <div className="hatch-dated-field">
                <label className="hatch-change-toggle">
                  <input
                    type="checkbox"
                    checked={form.setDue}
                    onChange={(e) => setForm({ ...form, setDue: e.target.checked })}
                  />
                  Change the due date
                </label>
                {form.setDue && (
                  <MomentField
                    label="Due"
                    hint="Leave the date empty to take it off."
                    value={form.dueAt}
                    onChange={(dueAt) => setForm({ ...form, dueAt })}
                  />
                )}
              </div>
            </div>

            <div className="hatch-form-actions">
              <span className="text-muted">
                {chosen.size} of {results.length} ticked
              </span>
              <Button
                variant="primary"
                loading={applying}
                disabled={!request}
                onClick={() => void apply()}
              >
                {isEmptyForm(form) ? 'Nothing to apply' : `Apply to ${chosen.size}`}
              </Button>
            </div>

            {outcome && (
              <div className="hatch-bulk-outcome">
                <p>{summarize(outcome)}</p>
                {outcome.failures.length > 0 && (
                  <ul className="hatch-bulk-failures">
                    {outcome.failures.map((failure) => (
                      <li key={failure.key}>
                        <code>{failure.key}</code> — {failure.reason}
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            )}
          </Card>

          <Card flush>
            <Table>
              <thead>
                <tr>
                  <th>
                    <input
                      type="checkbox"
                      aria-label="Tick every result"
                      checked={chosen.size === results.length}
                      onChange={(e) => setChosen(new Set(e.target.checked ? results.map((i) => i.key) : []))}
                    />
                  </th>
                  <th>Key</th>
                  <th>Type</th>
                  <th>Title</th>
                  <th>Status</th>
                  <th>Parent</th>
                </tr>
              </thead>
              <tbody>
                {results.map((issue) => {
                  const status = statuses.find((s) => s.id === issue.statusId);
                  return (
                    <tr key={issue.key}>
                      <td>
                        <input
                          type="checkbox"
                          aria-label={`Include ${issue.key}`}
                          checked={chosen.has(issue.key)}
                          onChange={(e) => {
                            const next = new Set(chosen);
                            if (e.target.checked) next.add(issue.key);
                            else next.delete(issue.key);
                            setChosen(next);
                          }}
                        />
                      </td>
                      <td>
                        <Link to={`/issues/${issue.key}`}>{issue.key}</Link>
                      </td>
                      <td>
                        <TypeBadge type={issue.type} />
                      </td>
                      <td>{truncate(issue.title, 120)}</td>
                      <td>{status && <StatusPill status={status} />}</td>
                      <td className="text-muted">{issue.parentKey ?? '—'}</td>
                    </tr>
                  );
                })}
              </tbody>
            </Table>
          </Card>
        </>
      )}
    </div>
  );
}
