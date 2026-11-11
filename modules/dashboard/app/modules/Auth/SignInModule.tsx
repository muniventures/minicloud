import { useLocation } from "react-router-dom";
import { safeReturnPath } from "../../api/accountApi";
import { AccountFlowForm } from "./AccountFlowForm";

export function SignInModule() {
  const location = useLocation();
  const returnTo = safeReturnPath((location.state as { from?: unknown } | null)?.from);
  return <AccountFlowForm kind="login" returnTo={returnTo} />;
}
