import { QueryClientProvider, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { BrowserRouter, NavLink, Outlet, Route, Routes, useLocation } from 'react-router-dom'
import { setUnauthorizedHandler } from './api/http'
import { createQueryClient, endSession } from './queryClient'
import RequireAuth from './components/RequireAuth'
import DashboardPage from './pages/DashboardPage'
import DepartmentPage from './pages/DepartmentPage'
import DepartmentsPage from './pages/DepartmentsPage'
import AdminHubPage from './pages/AdminHubPage'
import Landing from './pages/Landing'
import RequesterPage from './pages/RequesterPage'
import ProfilePage from './pages/ProfilePage'
import ResponsesPage from './pages/ResponsesPage'
import PortalHome from './pages/portal/PortalHome'
import { KnowledgeArticle, KnowledgeHome } from './pages/portal/PortalKnowledge'
import PortalRequest from './pages/portal/PortalRequest'
import PortalRequests from './pages/portal/PortalRequests'
import PortalSubmit from './pages/portal/PortalSubmit'
import PortalTopic from './pages/portal/PortalTopic'
import StatusPage from './pages/StatusPage'
import TeamPage from './pages/TeamPage'
import WorkspacePage from './pages/WorkspacePage'

function AdminLayout() {
  // Inside a department the page header carries its own breadcrumb; the switch between Departments and System status is for the top-level pages.
  const path = useLocation().pathname
  const inDepartment = path.startsWith('/admin/departments/')
  const inTeam = inDepartment && path.includes('/teams/')
  if (path === '/admin' || path === '/admin/') return <Outlet />
  return (
    <div className={inDepartment ? `page admin ${inTeam ? 'team-admin' : 'department-admin'}` : 'admin-layout'}>
      {!inDepartment && <aside className="admin-sidebar">
        <h2>Administration</h2>
        <span className="rail-heading">Organisation</span>
        <nav aria-label="Administration">
          <NavLink to="/admin" end>Settings hub</NavLink>
          <NavLink to="/admin/departments" end>Departments</NavLink>
          <span className="rail-heading">Platform</span>
          <NavLink to="/admin/status">System status</NavLink>
        </nav>
        <p className="sidebar-note">Department membership controls access to service data.</p>
      </aside>}
      {inDepartment ? <Outlet /> : <div className="page admin"><Outlet /></div>}
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
        <Route path="topics/:topicId" element={<PortalTopic />} />
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
        <Route path="dashboard/:departmentId?" element={<DashboardPage />} />
        <Route path="requesters/:requesterId" element={<RequesterPage />} />
        <Route path="responses" element={<ResponsesPage />} />
        <Route path="profile" element={<ProfilePage />} />
        <Route path="admin" element={<AdminLayout />}>
          <Route index element={<AdminHubPage />} />
          <Route path="departments" element={<DepartmentsPage />} />
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
