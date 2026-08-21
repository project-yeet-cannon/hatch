import { Navigate, Route, Routes } from 'react-router-dom';
import './App.css';
import { RedeemPage } from './pages/RedeemPage';
import { SignInPage } from './pages/SignInPage';

export function App() {
  return (
    <Routes>
      <Route path="/" element={<SignInPage />} />
      <Route path="/r/:code" element={<RedeemPage />} />
      {/* Anything else under /apps/auth is a stale link. The form is the only
          thing this app has to offer, so send them there rather than to a 404
          that tells someone who is already locked out to go away. */}
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
