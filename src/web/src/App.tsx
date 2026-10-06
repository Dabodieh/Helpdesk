import { QueryClientProvider, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { BrowserRouter, NavLink, Outlet, Route, Routes } from 'react-router-dom'
import { setUnauthorizedHandler } from './api/http'
import { createQueryClient, endSession } from './queryClient'
import RequireAuth from './components/RequireAuth'
import DepartmentPage from './pages/DepartmentPage'
import DepartmentsPage from './pages/DepartmentsPage'
import Landing from './pages/Landing'
import ResponsesPage from './pages/ResponsesPage'
import PortalHome from './pages/portal/PortalHome'
import { KnowledgeArticle, KnowledgeHome } from './pages/portal/PortalKnowledge'
import PortalRequest from './pages/portal/PortalRequest'
import PortalRequests from './pages/portal/PortalRequests'
import PortalSubmit from './pages/portal/PortalSubmit'
import StatusPage from './pages/StatusPage'
import TeamPage from './pages/TeamPage'
import WorkspacePage from './pages/WorkspacePage'

function AdminLayout() {
  return (
    <div className="page">
      <nav className="subnav" aria-label="Administration">
        <NavLink to="/admin" end>
          Departments
        </NavLink>
        <NavLink to="/admin/status">System status</NavLink>
      </nav>
      <Outlet />
    </div>
  )
}

export function AppRoutes() {
  const qc = useQueryClient()
  useEffect(() => {
    // Any 401 means the session ended: drop cached data and show the sign-in screen.
    setUnauthorizedHandler(() => endSession(qc))
    return () => setUnauthorizedHandler(null)
  }, [qc])

  return (
    <Routes>
      <Route path="portal" element={<RequireAuth layout="portal" />}>
        <Route index element={<PortalHome />} />
        <Route path="submit/:formId?" element={<PortalSubmit />} />
        <Route path="requests" element={<PortalRequests />} />
        <Route path="requests/:reference" element={<PortalRequest />} />
        <Route path="knowledge" element={<KnowledgeHome />} />
        <Route path="knowledge/:slug" element={<KnowledgeArticle />} />
      </Route>
      <Route element={<RequireAuth />}>
        <Route index element={<Landing />} />
        <Route path="queue/:queue/:ticket?" element={<WorkspacePage mode="queue" />} />
        <Route path="tickets/:ticket" element={<WorkspacePage mode="ticket" />} />
        <Route path="search/:ticket?" element={<WorkspacePage mode="search" />} />
        <Route path="responses" element={<ResponsesPage />} />
        <Route path="admin" element={<AdminLayout />}>
          <Route index element={<DepartmentsPage />} />
          <Route path="departments/:departmentId" element={<DepartmentPage />} />
          <Route path="departments/:departmentId/teams/:teamId" element={<TeamPage />} />
          <Route path="status" element={<StatusPage />} />
        </Route>
        <Route
          path="*"
          element={
            <div className="page">
              <p>Page not found.</p>
            </div>
          }
        />
      </Route>
    </Routes>
  )
}

export default function App() {
  const [client] = useState(createQueryClient)
  return (
    <QueryClientProvider client={client}>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </QueryClientProvider>
  )
}
