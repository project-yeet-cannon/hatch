import { useEffect, useState } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import { TopBar } from '@aerie/ui';
import './App.css';
import { getDocs } from './api/client';
import { DocList } from './components/DocList';
import { DocPage } from './pages/DocPage';
import type { DocSummary } from './types';

export function App() {
  const [docs, setDocs] = useState<DocSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getDocs()
      .then(setDocs)
      .catch((err: unknown) => setError(err instanceof Error ? err.message : String(err)));
  }, []);

  return (
    <div className="docs-app">
      {/* The shared bar. It replaces a 20px-padded block with a 22px <h1> and
          an "Architecture & design notes" subtitle - about 90px of chrome above
          a page whose whole job is to show a document. The subtitle is gone
          with it: the bar says where you are, and the doc below says what it
          is. The ▦ that used to sit here was a text glyph pretending to be a
          control; the bar's app-switcher is the real one. */}
      <TopBar appName="Aerie Docs" />

      <div className="docs-body">
        <nav className="docs-sidebar">
          {error && <p className="text-danger">{error}</p>}
          {!error && !docs && <p className="text-muted">Loading…</p>}
          {docs && <DocList docs={docs} />}
        </nav>

        <main className="docs-content">
          <Routes>
            <Route path="/" element={<IndexRoute docs={docs} />} />
            {/* Catch-all rather than ":slug": a slug is a path once docs live in subdirectories. */}
            <Route path="/*" element={<DocPage docs={docs ?? []} />} />
          </Routes>
        </main>
      </div>
    </div>
  );
}

function IndexRoute({ docs }: { docs: DocSummary[] | null }) {
  if (!docs) return null;
  if (docs.length === 0) return <p className="text-muted">No documents found.</p>;
  return <Navigate to={`/${docs[0].slug}`} replace />;
}
