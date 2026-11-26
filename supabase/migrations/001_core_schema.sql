create table if not exists public.user_profiles (
	user_id uuid primary key references auth.users (id) on delete cascade,
	username text not null,
	email text not null,
	created_at timestamptz not null default now(),
	constraint user_profiles_username_length check (length(username) between 8 and 64),
	constraint user_profiles_username_format check (
		username ~ '^[A-Za-z0-9_-]+$' and username ~ '[A-Z]' and username ~ '[0-9]'
	)
);

create unique index if not exists user_profiles_username_ci_idx on public.user_profiles (lower(username));

alter table public.user_profiles enable row level security;

drop policy if exists "Users can read their own profile" on public.user_profiles;
create policy "Users can read their own profile"
	on public.user_profiles for select to authenticated
	using (auth.uid() = user_id);

drop policy if exists "Users can create their own profile" on public.user_profiles;
create policy "Users can create their own profile"
	on public.user_profiles for insert to authenticated
	with check (auth.uid() = user_id);

drop policy if exists "Users can update their own profile" on public.user_profiles;
create policy "Users can update their own profile"
	on public.user_profiles for update to authenticated
	using (auth.uid() = user_id)
	with check (auth.uid() = user_id);

grant usage on schema public to authenticated;
grant select, insert, update on public.user_profiles to authenticated;

create table if not exists public.user_documents (
	user_id uuid not null references auth.users (id) on delete cascade,
	document_key text not null,
	payload jsonb not null,
	updated_at timestamptz not null default now(),
	primary key (user_id, document_key),
	constraint user_documents_key_not_empty check (length(document_key) between 1 and 512)
);

alter table public.user_documents enable row level security;

drop policy if exists "Users can read their own documents" on public.user_documents;
create policy "Users can read their own documents"
	on public.user_documents for select to authenticated
	using (auth.uid() = user_id);

drop policy if exists "Users can create their own documents" on public.user_documents;
create policy "Users can create their own documents"
	on public.user_documents for insert to authenticated
	with check (auth.uid() = user_id);

drop policy if exists "Users can update their own documents" on public.user_documents;
create policy "Users can update their own documents"
	on public.user_documents for update to authenticated
	using (auth.uid() = user_id)
	with check (auth.uid() = user_id);

drop policy if exists "Users can delete their own documents" on public.user_documents;
create policy "Users can delete their own documents"
	on public.user_documents for delete to authenticated
	using (auth.uid() = user_id);

grant select, insert, update, delete on public.user_documents to authenticated;
