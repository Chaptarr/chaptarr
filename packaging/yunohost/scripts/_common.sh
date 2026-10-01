#!/bin/bash

source /usr/share/yunohost/helpers

chaptarrng_install_runtime() {
    local debian_version
    debian_version="$(. /etc/os-release; printf '%s' "${VERSION_ID%%.*}")"

    case "$debian_version" in
        12|13) ;;
        *) ynh_die --message="ChaptarrNG requires Debian 12 or 13 for the .NET 10 runtime (found Debian $debian_version)." ;;
    esac

    ynh_apt_install_dependencies_from_extra_repository \
        --repo="https://packages.microsoft.com/debian/$debian_version/prod" \
        --package="aspnetcore-runtime-10.0" \
        --key="https://packages.microsoft.com/keys/microsoft.asc"
}

chaptarrng_prepare_service() {
    install -d -o "$app" -g "$app" -m 0700 "$data_dir" "$data_dir/tmp"
    ynh_multimedia_build_main_dir
    ynh_multimedia_addaccess "$app"
    ynh_config_add_nginx
    ynh_config_add_systemd
}

chaptarrng_register_service() {
    yunohost service add "$app" --description="ChaptarrNG audiobook library service"
}
