import { createContext, useContext, type ReactNode } from "react";
import { Breadcrumb, BreadcrumbItem, Heading } from "@carbon/react";

/** The navigation group of the current page, shown as the first breadcrumb. */
export const GroupContext = createContext<string>("");

/** Carbon page pattern: breadcrumb, page title, one line of context and the page's actions. */
export default function PageHeader({ title, description, actions }: { title: string; description?: string; actions?: ReactNode }) {
  const group = useContext(GroupContext);
  return (
    <div>
      {group && (
        <Breadcrumb noTrailingSlash>
          <BreadcrumbItem>{group}</BreadcrumbItem>
          <BreadcrumbItem isCurrentPage>{title}</BreadcrumbItem>
        </Breadcrumb>
      )}
      <div className="ao-page-head">
        <div>
          <Heading>{title}</Heading>
          {description && <p className="ao-muted">{description}</p>}
        </div>
        {actions && <div className="ao-actions">{actions}</div>}
      </div>
    </div>
  );
}
