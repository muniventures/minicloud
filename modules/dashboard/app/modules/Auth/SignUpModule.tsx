import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { Chrome } from "lucide-react";
import { FormEvent, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { signInWithGoogle, signUpWithEmail } from "../../core/auth/firebase";

export function SignUpModule() {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const navigate = useNavigate();

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setIsSubmitting(true);
    try {
      await signUpWithEmail(email, password);
      navigate("/onboarding", { replace: true });
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : "Sign up failed.");
    } finally {
      setIsSubmitting(false);
    }
  }

  async function googleSignUp() {
    setError(null);
    setIsSubmitting(true);
    try {
      await signInWithGoogle();
      navigate("/onboarding", { replace: true });
    } catch (submitError) {
      setError(submitError instanceof Error ? submitError.message : "Google sign up failed.");
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <Card className="w-full max-w-md">
      <CardHeader>
        <CardTitle>Create account</CardTitle>
        <CardDescription>Start your Minicloud workspace.</CardDescription>
      </CardHeader>
      <CardContent>
        <form className="space-y-4" onSubmit={submit}>
          <div className="space-y-2">
            <Label htmlFor="email">Email</Label>
            <Input id="email" onChange={(event) => setEmail(event.target.value)} required type="email" value={email} />
          </div>
          <div className="space-y-2">
            <Label htmlFor="password">Password</Label>
            <Input id="password" minLength={8} onChange={(event) => setPassword(event.target.value)} required type="password" value={password} />
          </div>
          {error ? <p className="text-sm text-destructive">{error}</p> : null}
          <Button className="w-full" disabled={isSubmitting} type="submit">
            Create account
          </Button>
        </form>
        <div className="my-4 flex items-center gap-3">
          <Separator className="flex-1" />
          <span className="text-xs text-muted-foreground">or</span>
          <Separator className="flex-1" />
        </div>
        <Button className="w-full" disabled={isSubmitting} onClick={googleSignUp} variant="outline">
          <Chrome className="h-4 w-4" />
          Continue with Google
        </Button>
        <p className="mt-4 text-center text-sm text-muted-foreground">
          Already have an account?{" "}
          <Link className="font-medium text-primary" to="/sign-in">
            Sign in
          </Link>
        </p>
      </CardContent>
    </Card>
  );
}
