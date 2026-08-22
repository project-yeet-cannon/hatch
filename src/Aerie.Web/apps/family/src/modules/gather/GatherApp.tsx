import { Navigate, Route, Routes } from 'react-router-dom';
import { ListPage } from './ListPage';
import { ListsPage } from './ListsPage';
import { listsPath } from './routes';
import './gather.css';

/**
 * Gather's slice of the shell. Everything under /apps/family/gather/ is routed
 * here and nowhere else.
 *
 * No subnav, unlike Storage: there is one hierarchy here - lists, then a list -
 * and a tab strip over a two-deep tree is chrome standing in for a back link.
 */
export default function GatherApp() {
  return (
    <Routes>
      <Route index element={<ListsPage />} />
      <Route path=":listId" element={<ListPage />} />
      <Route path="*" element={<Navigate to={listsPath} replace />} />
    </Routes>
  );
}
