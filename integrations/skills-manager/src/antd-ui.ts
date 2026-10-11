import type { Locale } from "antd/es/locale/index.js";
import darkAlgorithmImport from "antd/es/theme/themes/dark/index.js";
import defaultAlgorithmImport from "antd/es/theme/themes/default/index.js";
import type { ButtonProps } from "antd/es/button/Button.js";
import { useHostDark } from "./host-theme.js";
import ButtonImport from "antd/es/button/index.js";
import type { ConfigProviderProps } from "antd/es/config-provider/index.js";
import ConfigProviderImport from "antd/es/config-provider/index.js";
import type { DropdownProps } from "antd/es/dropdown/dropdown.js";
import DropdownImport from "antd/es/dropdown/index.js";
import type { InputProps } from "antd/es/input/Input.js";
import InputImport from "antd/es/input/index.js";
import type { TextAreaProps } from "antd/es/input/TextArea.js";
import TextAreaImport from "antd/es/input/TextArea.js";
import type { CollapseProps } from "antd/es/collapse/Collapse.js";
import CollapseImport from "antd/es/collapse/index.js";
import type { ListProps } from "antd/es/list/index.js";
import ListImport from "antd/es/list/index.js";
import type { ModalProps } from "antd/es/modal/interface.js";
import ModalImport from "antd/es/modal/index.js";
import type { ProgressProps } from "antd/es/progress/progress.js";
import ProgressImport from "antd/es/progress/index.js";
import type { SegmentedProps } from "antd/es/segmented/index.js";
import SegmentedImport from "antd/es/segmented/index.js";
import type { SelectProps } from "antd/es/select/index.js";
import SelectImport from "antd/es/select/index.js";
import type { SwitchProps } from "antd/es/switch/index.js";
import SwitchImport from "antd/es/switch/index.js";
import TagImport, { type TagProps } from "antd/es/tag/index.js";
import type { TooltipProps } from "antd/es/tooltip/index.js";
import TooltipImport from "antd/es/tooltip/index.js";
import React from "react";

function unwrap<T>(mod: unknown): T {
  const value = mod as { default?: T };
  return value.default ?? (mod as T);
}

const TextArea = unwrap<React.ComponentType<TextAreaProps>>(TextAreaImport);
export const Input = Object.assign(unwrap<React.ComponentType<InputProps>>(InputImport), { TextArea });
const AntButton = unwrap<React.ComponentType<ButtonProps>>(ButtonImport);

/** 页面操作按钮默认用圆角。调用处传入的 shape、size 可以覆盖。 */
export function Button(props: ButtonProps): React.ReactElement {
  return React.createElement(AntButton, { shape: "round", ...props });
}
type AntdCollapse = React.ComponentType<CollapseProps> & { Panel: React.ComponentType<Record<string, unknown>> };
type AntdListItem = React.ComponentType<Record<string, unknown>> & { Meta: React.ComponentType<Record<string, unknown>> };
type AntdList = React.ComponentType<ListProps<unknown>> & { Item: AntdListItem };
export const Collapse = unwrap<AntdCollapse>(CollapseImport);
export const ConfigProvider = unwrap<React.ComponentType<ConfigProviderProps>>(ConfigProviderImport);
export const Dropdown = unwrap<React.ComponentType<DropdownProps>>(DropdownImport);
export const List = unwrap<AntdList>(ListImport);
export const Modal = unwrap<React.ComponentType<ModalProps>>(ModalImport);
export const Progress = unwrap<React.ComponentType<ProgressProps>>(ProgressImport);
export const Segmented = unwrap<React.ComponentType<SegmentedProps>>(SegmentedImport);
export const Select = unwrap<React.ComponentType<SelectProps>>(SelectImport);
export const Switch = unwrap<React.ComponentType<SwitchProps>>(SwitchImport);
export const Tag = unwrap<React.ComponentType<TagProps>>(TagImport);
export const Tooltip = unwrap<React.ComponentType<TooltipProps>>(TooltipImport);
const darkAlgorithm = unwrap<NonNullable<ConfigProviderProps["theme"]> extends { algorithm?: infer Algorithm } ? Algorithm : never>(darkAlgorithmImport);
const defaultAlgorithm = unwrap<NonNullable<ConfigProviderProps["theme"]> extends { algorithm?: infer Algorithm } ? Algorithm : never>(defaultAlgorithmImport);

/** 按钮不插汉字空格，亮暗跟随宿主的 data-ds-dark-theme。主色保持 Ant Design 默认蓝。 */
export function AntdProvider(props: { locale?: Locale; children?: React.ReactNode }): React.ReactElement {
  const dark = useHostDark();
  return React.createElement(ConfigProvider, {
    locale: props.locale,
    button: { autoInsertSpace: false },
    theme: { algorithm: dark ? darkAlgorithm : defaultAlgorithm },
  }, props.children);
}
