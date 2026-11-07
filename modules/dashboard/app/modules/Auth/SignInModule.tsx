import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { Chrome } from "lucide-react";
import { FormEvent, useState } from "react";
import { Link, useLocation, useNavigate } from "react-router-dom";
import { signInWithEmail, signInWithGoogle } from "../../core/auth/firebase";

export function SignInModule() {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const navigate = useNavigate();
  const location = useLocation();
  const redirectTo = (location.state as { from?: string } | null)?.from ?? "/apps";

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setIsSubmitting(true);
    try {
      await signInWithEmail(email, password);
      navigate(redirectTo, { replace: true });
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : "Sign in failed.");
    } finally {
      setIsSubmitting(false);
    }
  }

  async function googleSignIn() {
    setError(null);
    setIsSubmitting(true);
    try {
      await signInWithGoogle();
      navigate(redirectTo, { replace: true });
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : "Google sign in failed.");
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <Card className="w-full max-w-md">
      <CardHeader>
        <CardTitle>Sign in</CardTitle>
        <CardDescription>Access your Minicloud control plane.</CardDescription>
      </CardHeader>
      <CardContent>
        <form className="space-y-4" onSubmit={submit}>
          <div className="space-y-2">
            <Label htmlFor="email">Email</Label>
            <Input id="email" onChange={(event) => setEmail(event.target.value)} required type="email" value={email} />
          </div>
          <div className="space-y-2">
            <Label htmlFor="password">Password</Label>
            <Input id="password" onChange={(event) => setPassword(event.target.value)} required type="password" value={password} />
          </div>
          {error ? <p className="text-sm text-destructive">{error}</p> : null}
          <Button className="w-full" disabled={isSubmitting} type="submit">
            Sign in
          </Button>
        </form>
        <div className="my-4 flex items-center gap-3">
          <Separator className="flex-1" />
          <span className="text-xs text-muted-foreground">or</span>
          <Separator className="flex-1" />
        </div>
        <Button className="w-full" disabled={isSubmitting} onClick={googleSignIn} variant="outline">
          <Chrome className="h-4 w-4" />
          Continue with Google
        </Button>
        <p className="mt-4 text-center text-sm text-muted-foreground">
          Need an account?{" "}
          <Link className="font-medium text-primary" to="/sign-up">
            Sign up
          </Link>
        </p>
      </CardContent>
    </Card>
  );
}
