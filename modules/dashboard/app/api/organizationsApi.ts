import { apiRequest } from "./apiClient";
import type { OrganizationResponse } from "../models/controlPlane";

export type CreateOrganizationRequest = {
  name: string;
  slug: string;
};

export function createOrganization(request: CreateOrganizationRequest) {
  return apiRequest<OrganizationResponse>("/api/organizations", {
    method: "POST",
    body: request
  });
}
