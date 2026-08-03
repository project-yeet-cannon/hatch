import { useEffect, useState } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
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
      <header className="docs-header">
        <a href="/" className="docs-back-link" title="Back to app picker">▦</a>
        <div className="docs-header-text">
          <h1>Aerie Docs</h1>
          <p>Architecture &amp; design notes</p>
        </div>
      </header>

      <div className="docs-body">
        <nav className="docs-sidebar">
          {error && <p className="text-danger">{error}</p>}
          {!error && !docs && <p className="text-muted">Loading…</p>}
          {docs && <DocList docs={docs} />}
        </nav>

        <main className="docs-content">
          <Routes>
            <Route path="/" element={<IndexRoute docs={docs} />} />
            <Route path="/:slug" element={<DocPage docs={docs ?? []} />} />
            <Route path="*" element={<p className="text-muted">Document not found.</p>} />
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
