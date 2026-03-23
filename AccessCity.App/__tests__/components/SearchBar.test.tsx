import React from 'react';
import { render, fireEvent } from '@testing-library/react-native';
import SearchBar from '@/components/MapView/SearchBar';

describe('SearchBar', () => {
  it('calls onChangeText and onSubmitEditing', () => {
    const onChangeText = jest.fn();
    const onSubmitEditing = jest.fn();

    const { getByPlaceholderText } = render(
      <SearchBar
        value=""
        onChangeText={onChangeText}
        onSubmitEditing={onSubmitEditing}
        onClear={jest.fn()}
      />,
    );

    fireEvent.changeText(getByPlaceholderText('Search destination in Birmingham…'), 'Birmingham');
    expect(onChangeText).toHaveBeenCalledWith('Birmingham');

    fireEvent(getByPlaceholderText('Search destination in Birmingham…'), 'submitEditing');
    expect(onSubmitEditing).toHaveBeenCalled();
  });

  it('invokes onSuggestionPress when suggestions are shown', () => {
    const onSuggestionPress = jest.fn();
    const suggestions = [{ id: '1', title: 'Birmingham', subtitle: 'UK' }];

    const { getByText } = render(
      <SearchBar
        value="b"
        onChangeText={jest.fn()}
        onSubmitEditing={jest.fn()}
        onClear={jest.fn()}
        suggestions={suggestions}
        onSuggestionPress={onSuggestionPress}
      />,
    );

    fireEvent.press(getByText('Birmingham'));
    expect(onSuggestionPress).toHaveBeenCalledWith(suggestions[0]);
  });
});
